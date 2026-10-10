/**
 * 部署工具的 git 操作层。
 *
 * 所有对 git 的调用都集中在这里，方便统一处理编码和错误。
 * 用 -c core.quotepath=false 保证中文文件名在 status 里不被转义成八进制，
 * 否则用户看到的是一串 "\345\233\276\207..." 完全无法辨认。
 */
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';

const exec = promisify(execFile);

const GIT_OPTS = ['-c', 'core.quotepath=false'];

export interface ChangedFile {
  /** 两字母状态码：M 修改 / A 新增 / D 删除 / ? 未跟踪 / R 重命名 */
  status: string;
  path: string;
  /** 已跟踪文件的增删行数；未跟踪文件为 null */
  stat: { added: number; removed: number } | null;
}

export interface GitState {
  branch: string;
  files: ChangedFile[];
  ahead: number;
  behind: number;
  /** 本地分支落后远端时为远端提交信息摘要，否则 null */
  diverged: boolean;
}

/** 跑一条 git 命令，失败时抛出带真实 stderr 的错误而不是静默返回空串。 */
async function git(args: string[], options: { allowFail?: boolean } = {}) {
  try {
    const { stdout } = await exec('git', [...GIT_OPTS, ...args], {
      encoding: 'utf8',
      maxBuffer: 32 * 1024 * 1024,
      windowsHide: true,
    });
    return stdout;
  } catch (error) {
    if (options.allowFail) return '';
    const detail = error instanceof Error ? error.message : String(error);
    throw new Error(`git ${args.join(' ')} 失败：${detail}`);
  }
}

/**
 * 跑一条会在终端里实时显示输出的命令（构建、单测），失败即抛错。
 *
 * 必须用 spawn 而不是 execFile：execFile 只接受管道，不支持 stdio:'inherit'，
 * 而构建日志需要直接打到当前终端才能让用户看到进度。
 */
export function runStreaming(command: string, args: string[], cwd: string) {
  return new Promise<void>((resolve, reject) => {
    // Windows 上 pnpm 是 .cmd 批处理，spawn 不带 shell 直接启动会失败
    // (EINVAL)。这里用固定的 cmd /d /s /c 显式调用，而不是 shell:true：
    // shell:true 会让 Node 对参数做字符串拼接，触发 DEP0190 且存在注入风险。
    // 命令和参数都是本工具内的常量，不含用户输入，拼接后逐个引号包裹。
    const isWin = process.platform === 'win32';
    const cmd = isWin ? process.env.ComSpec || 'cmd.exe' : command;
    const cmdArgs = isWin ? ['/d', '/s', '/c', [command, ...args].map(quoteArg).join(' ')] : args;

    const child = spawn(cmd, cmdArgs, {
      cwd,
      stdio: 'inherit',
      windowsHide: true,
    });

    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) {
        resolve();
        return;
      }
      reject(new Error(`退出码 ${code}`));
    });
  });
}

/** Windows cmd 的参数转义：含空格或特殊字符时用双引号包裹并转义内部引号。 */
function quoteArg(value: string): string {
  if (!/[\s"^&|<>()%!]/.test(value)) return value;
  return `"${value.replace(/"/g, '\\"')}"`;
}

const STATUS_MEANING: Record<string, string> = {
  M: '修改',
  A: '新增',
  D: '删除',
  R: '重命名',
  C: '复制',
  U: '冲突',
  '?': '未跟踪',
};

/** 把两字母状态码翻成人话，未知码原样返回。 */
export function describeStatus(status: string): string {
  if (status === '??') return STATUS_MEANING['?'];
  // 形如 "MM" / "AM"，两个字母表示索引区和工作区的状态，取第一个有意义的。
  const letters = status.split('');
  for (const letter of letters) {
    if (STATUS_MEANING[letter]) return STATUS_MEANING[letter];
  }
  return status;
}

/**
 * 解析 porcelain v1 的输出。
 *
 * 格式：XY<空格><路径>，重命名额外带 " -> <新路径>"。
 * 这里不用 -z，因为 -z 的 NUL 分隔在 Windows 上经过 shell 容易被吞掉，
 * 而路径里的引号转义在 -z 模式下不存在，反而更简单。
 */
function parsePorcelain(raw: string): ChangedFile[] {
  const files: ChangedFile[] = [];
  for (const line of raw.split('\n')) {
    if (!line.trim()) continue;
    if (line.startsWith('##')) continue;

    const status = line.slice(0, 2);
    let path = line.slice(3).trim();
    if (!path) continue;

    // 重命名/复制：处理 "旧路径 -> 新路径"
    if (status.includes('R') || status.includes('C')) {
      const arrow = path.lastIndexOf(' -> ');
      if (arrow > 0) path = path.slice(arrow + 4);
    }
    // 去掉可能残留的引号（路径含空格或中文时 git 会加引号）
    if (path.startsWith('"') && path.endsWith('"')) {
      path = path.slice(1, -1);
    }

    files.push({ status, path, stat: null });
  }
  return files;
}

/** 取工作区状态：分支、改动文件、与远端的领先/落后数。 */
export async function getState(): Promise<GitState> {
  const branchOut = await git(['rev-parse', '--abbrev-ref', 'HEAD']);
  const branch = branchOut.trim();

  const porcelain = await git(['status', '--porcelain']);
  const files = parsePorcelain(porcelain);

  // 已跟踪文件补上增删行数，方便用户判断改动大小。
  const tracked = files.filter((f) => f.status !== '??');
  if (tracked.length > 0) {
    const numstat = await git(['diff', 'HEAD', '--numstat'], { allowFail: true });
    const statMap = new Map<string, { added: number; removed: number }>();
    for (const line of numstat.split('\n')) {
      if (!line.trim()) continue;
      const [added, removed, ...rest] = line.split('\t');
      if (rest.length === 0) continue;
      const p = rest.join('\t');
      // 二进制文件的增删是 "-"，按 0 处理。
      statMap.set(p, {
        added: added === '-' ? 0 : Number(added) || 0,
        removed: removed === '-' ? 0 : Number(removed) || 0,
      });
    }
    for (const file of tracked) {
      file.stat = statMap.get(file.path) ?? null;
    }
  }

  // 与远端的领先/落后。用 @{upstream} 而不是硬编码 origin/main，
  // 这样换远端名或改上游跟踪都不会失效。
  const counts = await git(['rev-list', '--left-right', '--count', 'HEAD...@{upstream}'], { allowFail: true });
  const [aheadRaw, behindRaw] = counts.trim().split(/\s+/);
  const ahead = Number(aheadRaw) || 0;
  const behind = Number(behindRaw) || 0;

  return { branch, files, ahead, behind, diverged: ahead > 0 && behind > 0 };
}

/** 把指定路径加入暂存区。逐个 add 而不是 add .，避免误提交。 */
export async function stageFiles(paths: string[]): Promise<void> {
  if (paths.length === 0) return;
  await git(['add', '--', ...paths]);
}

export async function commit(message: string): Promise<void> {
  await git(['commit', '-m', message]);
}

/**
 * 推送当前分支到上游。
 * 用 --force-with-lease 而不是 --force：前者会在远端被别人推过新提交时
 * 拒绝覆盖，这是回滚场景下唯一安全的强推方式。
 */
export async function push(options: { force?: boolean } = {}): Promise<string> {
  const args = ['push'];
  if (options.force) args.push('--force-with-lease');
  return git(args);
}

/** 取最近 n 条提交的一行式摘要，供回滚确认时展示。 */
export async function recentCommits(n: number): Promise<{ hash: string; short: string; subject: string; date: string }[]> {
  const raw = await git(['log', `-${n}`, '--format=%h\t%ad\t%s', '--date=format:%Y-%m-%d %H:%M']);
  return raw
    .split('\n')
    .filter((line) => line.trim())
    .map((line) => {
      const [short, date, ...rest] = line.split('\t');
      return { hash: short ?? '', short: (short ?? '').slice(0, 7), subject: rest.join('\t'), date: date ?? '' };
    });
}

/** 取 HEAD 的完整 sha，回滚时要用。 */
export async function headSha(): Promise<string> {
  return (await git(['rev-parse', 'HEAD'])).trim();
}

/**
 * 暂存区是否为空。空的话 commit 会失败，先判断一下能给用户更好的提示。
 */
export async function hasStagedChanges(): Promise<boolean> {
  const staged = await git(['diff', '--cached', '--name-only'], { allowFail: true });
  return staged.trim().length > 0;
}

/**
 * 回滚：把本地重置到指定提交，再强推。
 *
 * 用 --force-with-lease 而不是 --force。区别在于前者会先检查远端是否
 * 仍是自己预期的那个提交，如果有人在回滚期间推了新提交，它会拒绝执行，
 * 避免把别人的改动无声覆盖掉。
 *
 * @param target 目标提交的短 sha
 * @param expectedHead 回滚前 HEAD 的完整 sha，作为 lease 的基准
 */
export async function resetAndPush(target: string, expectedHead: string): Promise<void> {
  await git(['reset', '--hard', target]);
  await git(['push', '--force-with-lease', `HEAD:${expectedHead}`]);
}
