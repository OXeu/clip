/** 在本机解码音频并压缩成时间轴可直接绘制的归一化峰值。 */
export async function extractWaveform(
  data: ArrayBuffer,
  bucketCount = 2400,
): Promise<Float32Array> {
  if (typeof AudioContext === 'undefined') return new Float32Array();
  const context = new AudioContext();
  try {
    const buffer = await context.decodeAudioData(data.slice(0));
    const buckets = Math.max(1, Math.min(bucketCount, buffer.length));
    const peaks = new Float32Array(buckets);
    const channels = Array.from(
      { length: buffer.numberOfChannels },
      (_, channel) => buffer.getChannelData(channel),
    );
    const stride = buffer.length / buckets;
    for (let bucket = 0; bucket < buckets; bucket++) {
      const start = Math.floor(bucket * stride);
      const end = Math.max(start + 1, Math.floor((bucket + 1) * stride));
      let peak = 0;
      // 长素材每桶最多采 96 个点；峰值图无需逐样本扫描。
      const step = Math.max(1, Math.floor((end - start) / 96));
      for (let sample = start; sample < end; sample += step) {
        for (const channel of channels) peak = Math.max(peak, Math.abs(channel[sample] ?? 0));
      }
      peaks[bucket] = Math.min(1, peak);
    }
    return peaks;
  } finally {
    void context.close();
  }
}
