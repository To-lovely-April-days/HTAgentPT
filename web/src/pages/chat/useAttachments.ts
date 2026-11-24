// 输入框里的附件：选文件/拖进来 → 先在前端挡掉格式、大小、个数不对的 → 上传 → 轮询解析状态。
// 还没发出的附件挂在输入框上；发问时等它们解析完、把 id 随问题带上，之后它们就跟着这条对话了。
import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, del, get, upload } from '../../lib/api';
import type { ChatAttachment } from '../../lib/types';

export const ATTACH_ACCEPT = '.docx,.docm,.xlsx,.xlsm,.pptx,.pdf,.png,.jpg,.jpeg,.webp,.gif,.bmp';
const IMAGE_EXTS = ['.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp'];
const EXTS = ATTACH_ACCEPT.split(',');
const MAX_BYTES = 20 * 1024 * 1024;
const MAX_FILES = 5;

/** 输入框上显示的一枚文件：上传中的还没有服务端 id，用本地 key。 */
export interface PendingFile {
  key: string;
  name: string;
  size: number;
  /** uploading=还在传；其余同服务端解析状态；error=前端或上传就失败了 */
  state: 'uploading' | 'parsing' | 'ready' | 'error';
  id?: string;
  note?: string;
}

const settled = (f: PendingFile) => f.state === 'ready' || f.state === 'error';

function fromServer(key: string, a: ChatAttachment): PendingFile {
  const state = a.status === 'Parsed' ? 'ready' : a.status === 'Failed' ? 'error' : 'parsing';
  const isImage = IMAGE_EXTS.includes(a.fileName.slice(a.fileName.lastIndexOf('.')).toLowerCase());
  const note = a.status === 'Parsed' ? (isImage ? '图片，问到时看图' : `${a.charCount.toLocaleString()} 字`)
    : a.status === 'Waiting' ? '解析引擎忙，稍后自动重试'
    : a.error ?? undefined;
  return { key, name: a.fileName, size: a.size, state, id: a.id, note };
}

export function formatSize(n: number): string {
  return n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(n / 1024))} KB`;
}

export function useAttachments() {
  const [files, setFiles] = useState<PendingFile[]>([]);
  // 轮询与发问时的等待要读到最新的列表，经 ref 拿（渲染后同步）
  const filesRef = useRef(files);
  useEffect(() => { filesRef.current = files; }, [files]);
  const seq = useRef(0);

  const patch = (key: string, p: Partial<PendingFile> | ((f: PendingFile) => PendingFile)) =>
    setFiles((xs) => xs.map((f) => (f.key === key ? (typeof p === 'function' ? p(f) : { ...f, ...p }) : f)));

  const add = useCallback((list: FileList | File[]) => {
    const incoming = Array.from(list);
    const room = MAX_FILES - filesRef.current.filter((f) => f.state !== 'error').length;
    incoming.forEach((file, i) => {
      const key = `f${++seq.current}`;
      const ext = file.name.slice(file.name.lastIndexOf('.')).toLowerCase();
      const reject = i >= room ? `一次最多带 ${MAX_FILES} 个文件`
        : ['.doc', '.xls', '.ppt', '.wps'].includes(ext) ? '老格式读不了，请另存为新格式再传'
        : !EXTS.includes(ext) ? '格式不支持（可传 Word、Excel、PowerPoint、PDF、图片）'
        : file.size > MAX_BYTES ? '超过 20MB'
        : file.size === 0 ? '空文件' : null;
      const item: PendingFile = { key, name: file.name, size: file.size, state: reject ? 'error' : 'uploading', note: reject ?? undefined };
      setFiles((xs) => [...xs, item]);
      if (reject) return;
      const form = new FormData();
      form.append('file', file);
      upload<ChatAttachment>('/api/chat/attachments', form)
        .then((a) => patch(key, fromServer(key, a)))
        .catch((e) => patch(key, { state: 'error', note: e instanceof ApiError ? e.message : '上传失败' }));
    });
  }, []);

  const remove = useCallback((key: string) => {
    const f = filesRef.current.find((x) => x.key === key);
    setFiles((xs) => xs.filter((x) => x.key !== key));
    if (f?.id) void del(`/api/chat/attachments/${f.id}`).catch(() => { /* 撤不掉也不挡输入 */ });
  }, []);

  // 解析中的轮询：每 1.5 秒查一次还没出结果的
  const parsing = files.some((f) => f.state === 'parsing');
  useEffect(() => {
    if (!parsing) return;
    const t = setInterval(() => {
      for (const f of filesRef.current.filter((x) => x.state === 'parsing' && x.id)) {
        get<ChatAttachment>(`/api/chat/attachments/${f.id}`)
          .then((a) => patch(f.key, fromServer(f.key, a)))
          .catch(() => { /* 下一轮再查 */ });
      }
    }, 1500);
    return () => clearInterval(t);
  }, [parsing]);

  /** 发问时用：等这一批都有结果（最多等 3 分钟），交出解析好的 id，清空输入框上的文件。 */
  const take = useCallback(async (): Promise<{ ids: string[]; names: string[] }> => {
    const deadline = Date.now() + 180_000;
    while (filesRef.current.some((f) => !settled(f)) && Date.now() < deadline)
      await new Promise((r) => setTimeout(r, 500));
    const ready = filesRef.current.filter((f) => f.state === 'ready' && f.id);
    setFiles([]);
    return { ids: ready.map((f) => f.id!), names: ready.map((f) => f.name) };
  }, []);

  const clear = useCallback(() => {
    for (const f of filesRef.current) if (f.id) void del(`/api/chat/attachments/${f.id}`).catch(() => {});
    setFiles([]);
  }, []);

  return { files, add, remove, take, clear, busy: files.some((f) => !settled(f)) };
}
