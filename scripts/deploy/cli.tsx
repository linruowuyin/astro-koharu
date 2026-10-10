/**
 * 博客部署工具。
 *
 * 替代原来的 更新.bat。核心区别：
 *   1. 不再用 git add . 无差别全提交，而是列出改动让你逐项勾选
 *   2. 推送前强制跑构建 + 单测，不通过就阻断
 *   3. 提供 rollback 子命令，一键把线上退回上一个提交
 *
 * 用法：
 *   pnpm deploy            发布当前改动
 *   pnpm deploy --dry-run  只演练，不提交不推送
 *   pnpm deploy rollback   回滚线上到上一个提交
 *   pnpm deploy status     查看当前状态
 */

import process from 'node:process';
import { Box, render, Text } from 'ink';
import { useEffect, useState } from 'react';
import {
  type ChangedFile,
  commit,
  describeStatus,
  getState,
  hasStagedChanges,
  headSha,
  push,
  recentCommits,
  resetAndPush,
  runStreaming,
  stageFiles,
} from './git-ops';
import { ConfirmPrompt, FileSelector, TextPrompt } from './ui';

const PROJECT_ROOT = process.cwd();
const DRY_RUN = process.argv.includes('--dry-run');

/**
 * 非交互模式的两个开关，用于 CI 或脚本化场景：
 *   --files a.ts,b.astro   直接指定要提交的文件，跳过勾选界面
 *   --message "..."        直接指定提交信息
 * 两个都给时，整个流程不进入交互，可以直接跑完。
 */
const FILES_ARG = readFlag('--files');
const MESSAGE_ARG = readFlag('--message');

function readFlag(flag: string): string | null {
  const prefix = `${flag}=`;
  const inline = process.argv.find((arg) => arg.startsWith(prefix));
  if (inline) return inline.slice(prefix.length);
  const index = process.argv.indexOf(flag);
  if (index >= 0 && process.argv[index + 1]) return process.argv[index + 1];
  return null;
}

type Step =
  | { name: 'loading' }
  | { name: 'select-files' }
  | { name: 'commit-message'; files: ChangedFile[] }
  | { name: 'confirm'; message: string; files: ChangedFile[] }
  | { name: 'checking' }
  | { name: 'pushing' }
  | { name: 'done'; message: string; isError?: boolean }
  | { name: 'cancelled' };

/** 按改动类型统计，给用户一个总览。 */
function summarize(files: ChangedFile[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const file of files) {
    const label = describeStatus(file.status);
    counts[label] = (counts[label] ?? 0) + 1;
  }
  return counts;
}

async function runChecks(): Promise<{ ok: boolean; output: string }> {
  const steps: Array<{ name: string; cmd: string; args: string[] }> = [
    // CI=true 让 pnpm 不进入交互式安装确认；构建要跑 migrate --check。
    { name: '构建', cmd: 'pnpm', args: ['build'] },
    { name: '单测', cmd: 'pnpm', args: ['test:index'] },
  ];

  for (const step of steps) {
    process.stdout.write(`\n\x1b[36m▸ ${step.name}\x1b[0m ${step.cmd} ${step.args.join(' ')}\n`);
    try {
      await runStreaming(step.cmd, step.args, PROJECT_ROOT);
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      return { ok: false, output: `${step.name}失败：${detail}` };
    }
  }
  return { ok: true, output: '构建与单测全部通过' };
}

function DeployApp() {
  const [step, setStep] = useState<Step>({ name: 'loading' });
  const [state, setState] = useState<Awaited<ReturnType<typeof getState>> | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  // 读一次 git 状态。必须放在 effect 里：写在渲染函数体内会在每次
  // 重渲染时重复发起 Promise，既浪费又可能造成状态抖动。
  useEffect(() => {
    let cancelled = false;
    getState()
      .then((result) => {
        if (cancelled) return;
        setState(result);
        if (result.files.length === 0) {
          setStep({
            name: 'done',
            message:
              result.behind > 0
                ? `工作区干净，但本地落后远端 ${result.behind} 个提交，请先 git pull`
                : '工作区干净，没有需要提交的改动',
          });
          return;
        }
        if (result.diverged) {
          setStep({
            name: 'done',
            message: `本地与远端已分叉（领先 ${result.ahead} / 落后 ${result.behind}），请先 git pull --rebase 解决`,
            isError: true,
          });
          return;
        }
        setStep({ name: 'select-files' });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        setLoadError(String(error));
        setStep({ name: 'done', message: `读取 git 状态失败：${String(error)}`, isError: true });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (loadError) {
    return (
      <Box flexDirection="column">
        <Text color="red">✗ 无法读取仓库状态</Text>
        <Text dimColor>{loadError}</Text>
      </Box>
    );
  }

  if (step.name === 'loading') {
    return <Text dimColor>读取仓库状态…</Text>;
  }

  // ---------- 选择文件 ----------
  if (step.name === 'select-files' && state) {
    return (
      <Box flexDirection="column">
        <Text dimColor>
          分支 {state.branch}
          {state.upstreamMissing ? ' · 上游未配置' : ` · 领先 ${state.ahead} / 落后 ${state.behind}`}
        </Text>
        <Box marginTop={1}>
          <FileSelector
            files={state.files}
            onCancel={() => setStep({ name: 'cancelled' })}
            onConfirm={(files) => {
              if (files.length === 0) {
                setStep({ name: 'done', message: '未选择任何文件，已取消', isError: true });
                return;
              }
              setStep({ name: 'commit-message', files });
            }}
          />
        </Box>
      </Box>
    );
  }

  // ---------- 填写提交信息 ----------
  if (step.name === 'commit-message') {
    return (
      <TextPrompt
        label="提交信息"
        placeholder="例如：feat: 新增两篇复盘笔记"
        onCancel={() => setStep({ name: 'select-files' })}
        onSubmit={(message) => {
          if (!message) {
            setStep({ name: 'done', message: '提交信息不能为空，已取消', isError: true });
            return;
          }
          setStep({ name: 'confirm', message, files: step.files });
        }}
      />
    );
  }

  // ---------- 推送前确认 ----------
  if (step.name === 'confirm') {
    // 提前取出，onConfirmed 是异步回调，期间 step 会被新状态覆盖，
    // 回调里再读 step.message 会拿到错误的值。
    const { message: commitMessage, files: selectedFiles } = step;
    return (
      <ConfirmStep
        message={commitMessage}
        files={selectedFiles}
        dryRun={DRY_RUN}
        onBack={() => setStep({ name: 'select-files' })}
        onCancel={() => setStep({ name: 'cancelled' })}
        onConfirmed={async (files) => {
          setStep({ name: 'checking' });
          const result = await runChecks();
          if (!result.ok) {
            setStep({ name: 'done', message: `${result.output}\n\n已阻断推送，修改后重新运行。`, isError: true });
            return;
          }
          if (DRY_RUN) {
            setStep({ name: 'done', message: `演练模式：检查已通过，跳过提交与推送。\n\n${files.length} 个文件将被提交。` });
            return;
          }

          setStep({ name: 'pushing' });
          try {
            await stageFiles(files.map((f) => f.path));
            await commit(commitMessage);
            await push();
            setStep({ name: 'done', message: '已推送到 origin/main\nCloudflare 将自动构建，约 1-2 分钟后生效。' });
          } catch (error) {
            const detail = error instanceof Error ? error.message : String(error);
            setStep({ name: 'done', message: `提交或推送失败：${detail}`, isError: true });
          }
        }}
      />
    );
  }

  // ---------- 检查中 ----------
  if (step.name === 'checking') {
    return (
      <Box flexDirection="column">
        <Text bold color="cyan">
          推送前检查
        </Text>
        <Text dimColor>正在跑构建与单测，请稍候（约 20 秒）…</Text>
      </Box>
    );
  }

  // ---------- 推送中 ----------
  if (step.name === 'pushing') {
    return (
      <Box flexDirection="column">
        <Text bold color="cyan">
          提交并推送
        </Text>
        <Text dimColor>正在推送到远端…</Text>
      </Box>
    );
  }

  // ---------- 终态 ----------
  if (step.name === 'done') {
    return (
      <Box flexDirection="column">
        <Text bold color={step.isError ? 'red' : 'green'}>
          {step.isError ? '✗ 未完成' : '✓ 完成'}
        </Text>
        <Box marginTop={1}>
          <Text>{step.message}</Text>
        </Box>
      </Box>
    );
  }

  return <Text dimColor>已取消，未做任何改动。</Text>;
}

/** 推送前的最终确认：显示提交信息与待提交文件数，默认否。 */
function ConfirmStep({
  message,
  dryRun,
  files,
  onBack,
  onCancel,
  onConfirmed,
}: {
  message: string;
  dryRun: boolean;
  files: ChangedFile[];
  onBack: () => void;
  onCancel: () => void;
  onConfirmed: (files: ChangedFile[]) => Promise<void>;
}) {
  return (
    <Box flexDirection="column">
      <Box marginBottom={1}>
        <Text>
          提交信息：
          <Text bold color="green">
            {message}
          </Text>
        </Text>
        <Text dimColor>将提交 {files.length} 个文件</Text>
        {dryRun ? <Text color="yellow">演练模式：不会真的提交或推送</Text> : null}
      </Box>
      <ConfirmPrompt
        message="确认推送？推送后 Cloudflare 会立即开始构建"
        onConfirm={() => onConfirmed(files)}
        onCancel={onCancel}
        onBack={onBack}
      />
      <Box marginTop={1}>
        <Text dimColor>按 Esc 返回上一步重新选择文件</Text>
      </Box>
    </Box>
  );
}

// ============ 子命令 ============

async function cmdStatus() {
  const state = await getState();
  const counts = summarize(state.files);
  console.log(`\n分支：${state.branch}`);
  if (state.upstreamMissing) {
    // 上游缺失时不能报 "领先 0 / 落后 0"，那会让人误以为和远端一致。
    console.log('上游追踪：未配置（首次推送时会自动建立）');
  } else {
    console.log(`领先远端：${state.ahead}  落后远端：${state.behind}`);
  }
  console.log(`改动文件：${state.files.length} 项`);
  for (const [label, n] of Object.entries(counts)) console.log(`  ${label}：${n}`);
  if (state.files.length > 0) {
    console.log('');
    for (const f of state.files) {
      console.log(`  ${describeStatus(f.status).padEnd(4, '　')} ${f.path}`);
    }
  }
  const commits = await recentCommits(5);
  if (commits.length > 0) {
    console.log('\n最近提交：');
    for (const c of commits) console.log(`  ${c.short}  ${c.date}  ${c.subject}`);
  }
  console.log('');
}

async function cmdRollback() {
  console.log('\n最近提交：');
  const commits = await recentCommits(8);
  commits.forEach((c, i) => {
    const marker = i === 0 ? '  ← 当前线上' : i === 1 ? '  ← 将回滚到这里' : '';
    console.log(`  ${c.short}  ${c.date}  ${c.subject}${marker}`);
  });

  if (commits.length < 2) {
    console.log('\n提交历史不足两个，无法回滚。\n');
    return;
  }

  const state = await getState();
  if (state.files.length > 0) {
    console.log(`\n\x1b[33m⚠ 工作区还有 ${state.files.length} 项未提交改动，回滚前请先处理，否则可能丢失。\x1b[0m`);
    console.log('  如需保留改动，先执行 pnpm deploy 提交它们。\n');
    return;
  }

  const target = commits[1];
  const before = await headSha();
  console.log(`\n即将把线上从 ${commits[0].short} 回滚到 ${target.short}（${target.subject}）`);
  console.log('\n输入 yes 确认执行：');

  const answer = await promptLine();
  if (answer.trim().toLowerCase() !== 'yes') {
    console.log('已取消，未做任何改动。\n');
    return;
  }

  try {
    await resetAndPush(target.short, before);
    console.log(`\n✓ 已回滚线上到 ${target.short}`);
    console.log('  Cloudflare 将自动重新构建，约 1-2 分钟后生效。');
    console.log(`  如需撤销这次回滚，可执行：git reset --hard ${before.slice(0, 7)} && git push --force-with-lease\n`);
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    console.error(`\n✗ 回滚失败：${detail}\n`);
    process.exitCode = 1;
  }
}

/** 读一行输入。回滚确认需要用户显式敲 yes，不能靠回车默认放行。 */
function promptLine(): Promise<string> {
  return new Promise((resolve) => {
    process.stdin.setEncoding('utf8');
    process.stdin.resume();
    process.stdin.once('data', (chunk) => {
      process.stdin.pause();
      resolve(String(chunk));
    });
  });
}

/**
 * 非交互发布：指定了 --files 就走这条路径，不渲染 ink 界面。
 * 适合 CI、以及没有 TTY 的自动化环境。
 */
async function cmdNonInteractive(): Promise<boolean> {
  if (!FILES_ARG) return false;

  const wanted = FILES_ARG.split(',')
    .map((p) => p.trim())
    .filter(Boolean);
  if (wanted.length === 0) {
    console.error('--files 参数为空');
    process.exitCode = 1;
    return true;
  }

  const state = await getState();
  const known = new Map(state.files.map((f) => [f.path, f]));
  const selected: ChangedFile[] = [];

  for (const path of wanted) {
    const file = known.get(path);
    if (!file) {
      // 不在改动列表里的文件可能是新增的（porcelain 报了 ??），也可能写错了。
      // 区分清楚再报错，避免用户误以为工具坏了。
      console.error(`✗ ${path} 不在当前改动列表里`);
      console.error(`  当前改动：${state.files.map((f) => f.path).join('、') || '（无）'}`);
      process.exitCode = 1;
      return true;
    }
    selected.push(file);
  }

  const message = MESSAGE_ARG;
  if (!message) {
    console.error('✗ 非交互模式必须同时提供 --message');
    process.exitCode = 1;
    return true;
  }

  console.log(`待提交 ${selected.length} 个文件：`);
  for (const f of selected) console.log(`  ${describeStatus(f.status).padEnd(4, '　')} ${f.path}`);
  console.log(`提交信息：${message}\n`);

  console.log('▸ 检查：构建');
  const result = await runChecks();
  if (!result.ok) {
    console.error(`\n✗ ${result.output}`);
    console.error('已阻断推送，修改后重新运行。');
    process.exitCode = 1;
    return true;
  }

  if (DRY_RUN) {
    console.log('\n✓ 演练模式：检查通过，未提交未推送。');
    return true;
  }

  try {
    await stageFiles(selected.map((f) => f.path));
    await commit(message);
    await push();
    console.log('\n✓ 已推送到 origin/main');
    console.log('  Cloudflare 将自动构建，约 1-2 分钟后生效。');
  } catch (error) {
    const detail = error instanceof Error ? error.message : String(error);
    console.error(`\n✗ 提交或推送失败：${detail}`);
    process.exitCode = 1;
  }
  return true;
}

async function cmdHelp() {
  console.log(`
部署工具

用法：
  pnpm deploy            发布改动（勾选文件 → 写提交信息 → 检查 → 推送）
  pnpm deploy --dry-run  演练：跑到检查为止，不提交不推送
  pnpm deploy status     查看当前分支、改动与最近提交
  pnpm deploy rollback   回滚线上到上一个提交

非交互（适合 CI / 无 TTY 环境）：
  pnpm deploy --files a.ts,b.astro --message "feat: xxx"

安全约束：
  · 只提交你勾选的文件，不会像旧的 bat 那样 git add . 全量提交
  · 推送前强制跑构建与单测，不通过会阻断
  · 回滚用 --force-with-lease，若远端有他人新提交会拒绝覆盖
`);
}

// ============ 入口 ============

async function main() {
  const sub = process.argv[2];

  if (sub === 'status') return cmdStatus();
  if (sub === 'rollback') return cmdRollback();
  if (sub === 'help' || sub === '--help' || sub === '-h') return cmdHelp();

  // 指定了 --files 就走非交互路径，避开 ink 的 raw mode 依赖。
  if (await cmdNonInteractive()) return;

  const instance = render(<DeployApp />);
  await instance.waitUntilExit();
}

main().catch((error: unknown) => {
  console.error('部署工具出错：', error);
  process.exitCode = 1;
});

export { main, cmdRollback, runChecks, hasStagedChanges };
