/** WebCodecs 编码器共同暴露的最小队列接口。 */
export interface CodecQueue extends EventTarget {
  readonly encodeQueueSize: number;
}

const checkAborted = (signal?: AbortSignal): void => {
  if (signal?.aborted) throw new DOMException('已取消导出', 'AbortError');
};

/**
 * 等待编码队列回落到高水位以下。
 *
 * 不使用 setTimeout 轮询：后台标签页会强烈节流计时器，队列一旦达到高水位，
 * 轮询式等待就会表现得像整个导出被暂停。WebCodecs 的 dequeue 事件专门用于
 * 通知队列尺寸下降，并且不会人为把每次恢复绑定到页面计时器。
 */
export async function waitForCodecCapacity(
  codec: CodecQueue,
  highWaterMark: number,
  signal?: AbortSignal,
): Promise<void> {
  checkAborted(signal);
  while (codec.encodeQueueSize > highWaterMark) {
    await new Promise<void>((resolve, reject) => {
      let settled = false;
      const cleanup = (): void => {
        codec.removeEventListener('dequeue', onDequeue);
        signal?.removeEventListener('abort', onAbort);
      };
      const finish = (callback: () => void): void => {
        if (settled) return;
        settled = true;
        cleanup();
        callback();
      };
      const onDequeue = (): void => finish(resolve);
      const onAbort = (): void => finish(() => reject(new DOMException('已取消导出', 'AbortError')));

      codec.addEventListener('dequeue', onDequeue, { once: true });
      signal?.addEventListener('abort', onAbort, { once: true });
      // 防止读取队列尺寸与注册事件之间发生 dequeue 而错过唤醒。
      if (codec.encodeQueueSize <= highWaterMark) onDequeue();
    });
    checkAborted(signal);
  }
}
