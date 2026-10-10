/**
 * 部署流程的交互组件。
 *
 * 交互设计的核心是「让用户在推送前看清楚到底要提交什么」——
 * 原来的 bat 用 git add . 无差别全提交，误提交无从察觉。
 */
import { Box, Text, useInput } from 'ink';
import { useState } from 'react';
import { type ChangedFile, describeStatus } from './git-ops';

const STATUS_COLOR: Record<string, string> = {
  新增: 'green',
  修改: 'yellow',
  删除: 'red',
  重命名: 'cyan',
  复制: 'cyan',
  冲突: 'red',
  未跟踪: 'gray',
};

function statusColor(file: ChangedFile): string {
  return STATUS_COLOR[describeStatus(file.status)] ?? 'white';
}

function formatStat(file: ChangedFile): string {
  if (!file.stat) return '';
  const { added, removed } = file.stat;
  if (added === 0 && removed === 0) return '';
  const parts: string[] = [];
  if (added > 0) parts.push(`+${added}`);
  if (removed > 0) parts.push(`-${removed}`);
  return parts.join(' ');
}

/**
 * 多选文件列表。
 *
 * 操作：上下移动 / 空格勾选 / a 全选 / n 全不选 / 回车确认。
 * 列表超出终端高度时只显示可视窗口并显示滚动提示。
 */
export function FileSelector({
  files,
  onConfirm,
  onCancel,
}: {
  files: ChangedFile[];
  onConfirm: (selected: ChangedFile[]) => void;
  onCancel: () => void;
}) {
  const [cursor, setCursor] = useState(0);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [showAll, setShowAll] = useState(false);

  // 视口高度留出上下留白和状态栏，读 process.stdout 在 ink 里是安全的。
  const rows = Math.max(5, (process.stdout.rows ?? 24) - 8);
  const needsScroll = files.length > rows && !showAll;
  const visible = needsScroll ? files.slice(cursor, cursor + rows) : files;

  useInput((input, key) => {
    if (key.escape) {
      onCancel();
      return;
    }
    if (key.upArrow) {
      setCursor((c) => (c === 0 ? files.length - 1 : c - 1));
      return;
    }
    if (key.downArrow) {
      setCursor((c) => (c === files.length - 1 ? 0 : c + 1));
      return;
    }
    if (input === ' ') {
      setSelected((prev) => {
        const next = new Set(prev);
        if (next.has(cursor)) next.delete(cursor);
        else next.add(cursor);
        return next;
      });
      return;
    }
    if (input === 'a') {
      setSelected(new Set(files.map((_, i) => i)));
      return;
    }
    if (input === 'n') {
      setSelected(new Set());
      return;
    }
    if (input === 'e') {
      // 展开/收起：文件多时默认只显示可视区，需要时展开全部。
      setShowAll((v) => !v);
      return;
    }
    if (key.return) {
      onConfirm(files.filter((_, i) => selected.has(i)));
    }
  });

  const selectedCount = selected.size;

  return (
    <Box flexDirection="column">
      <Box marginBottom={1}>
        <Text bold color="cyan">
          待提交文件
        </Text>
        <Text dimColor>
          （{files.length} 项，已选 {selectedCount}）
        </Text>
      </Box>

      {needsScroll ? (
        <Text dimColor>
          {' '}
          （第 {cursor + 1} 项，共 {files.length} 项，按 e 展开全部）
        </Text>
      ) : null}

      <Box flexDirection="column" marginBottom={1}>
        {visible.map((file, i) => {
          const realIndex = needsScroll ? cursor + i : i;
          const isCursor = realIndex === cursor;
          const isSelected = selected.has(realIndex);
          const label = describeStatus(file.status);
          const stat = formatStat(file);

          return (
            // 用真实索引做 key：路径可能重复（删除后同名重建），且展开/收起
            // 切换时 visible 的索引来源会变，用 path 会产生重复 key。
            <Box key={realIndex}>
              <Text color={isCursor ? 'cyan' : undefined}>{isCursor ? '❯ ' : '  '}</Text>
              <Text color={isSelected ? 'green' : 'gray'}>{isSelected ? '[x]' : '[ ]'}</Text>
              <Text> </Text>
              <Text color={statusColor(file)}>{label.padEnd(4, '　')}</Text>
              <Text> </Text>
              <Text bold={isCursor} color={isCursor ? 'white' : undefined}>
                {file.path}
              </Text>
              {stat ? <Text color="gray"> {stat}</Text> : null}
            </Box>
          );
        })}
      </Box>

      <Box>
        <Text dimColor>↑↓ 移动 · 空格 勾选 · a 全选 · n 全不选 · e 展开/收起 · 回车 确认 · Esc 取消</Text>
      </Box>
    </Box>
  );
}

/** 单行文本输入，用于填写提交信息。 */
export function TextPrompt({
  label,
  placeholder,
  defaultValue,
  onSubmit,
  onCancel,
}: {
  label: string;
  placeholder?: string;
  defaultValue?: string;
  onSubmit: (value: string) => void;
  onCancel: () => void;
}) {
  const [value, setValue] = useState(defaultValue ?? '');

  useInput((input, key) => {
    if (key.escape) {
      onCancel();
      return;
    }
    if (key.return) {
      onSubmit(value.trim());
      return;
    }
    if (key.backspace || key.delete) {
      setValue((v) => v.slice(0, -1));
      return;
    }
    // 方向键交给外层（ink 的焦点管理），这里只处理可打印字符。
    if (key.leftArrow || key.rightArrow || key.upArrow || key.downArrow) return;
    if (input && !key.ctrl && !key.meta) {
      setValue((v) => v + input);
    }
  });

  return (
    <Box flexDirection="column">
      <Text bold color="cyan">
        {label}
      </Text>
      {placeholder && !value ? (
        <Box>
          <Text dimColor>{placeholder}</Text>
          <Text inverse> </Text>
        </Box>
      ) : null}
      <Text>
        {value}
        <Text inverse> </Text>
      </Text>
      <Text dimColor>回车 确认 · Esc 取消</Text>
    </Box>
  );
}

/** 是/否确认。默认 No，避免误触直接推生产。Esc 返回上一步而不是直接放弃。 */
export function ConfirmPrompt({
  message,
  onConfirm,
  onCancel,
  onBack,
}: {
  message: string;
  onConfirm: () => void;
  onCancel: () => void;
  onBack?: () => void;
}) {
  const [confirmed, setConfirmed] = useState(false);

  useInput((input, key) => {
    if (key.escape) {
      // 有上一步就返回上一步，没有才当作取消。
      if (onBack) {
        onBack();
      } else {
        onCancel();
      }
      return;
    }
    if (input === 'y') {
      setConfirmed(true);
      return;
    }
    if (input === 'n') {
      setConfirmed(false);
      return;
    }
    if (key.leftArrow || key.rightArrow || input === 'h' || input === 'l') {
      setConfirmed((v) => !v);
      return;
    }
    if (key.return && confirmed) {
      onConfirm();
    }
  });

  return (
    <Box flexDirection="column">
      <Text bold color="yellow">
        {message}
      </Text>
      <Box marginTop={1}>
        <Text color={confirmed ? 'green' : 'gray'}>{confirmed ? '❯ 是' : '  是'}</Text>
        <Text> </Text>
        <Text color={!confirmed ? 'red' : 'gray'}>{!confirmed ? '❯ 否' : '  否'}</Text>
      </Box>
      <Text dimColor>←→ 或 y/n 切换 · 回车 确认（默认否）· Esc 取消</Text>
    </Box>
  );
}
