/**
 * mp4box.js 的最小类型声明。
 * 上游包没有附带类型，这里只声明实际用到的 API，避免使用 any 逃逸检查。
 */
declare module 'mp4box' {
  export interface MP4MediaTrack {
    id: number;
    type: string;
    codec: string;
    timescale: number;
    duration: number;
    nb_samples: number;
    /** 样本本身的总时长；不含 QuickTime edit list 对轨道时长的修正。 */
    samples_duration?: number;
    video?: { width: number; height: number };
    audio?: { sample_rate: number; channel_count: number; sample_size: number };
    track_width?: number;
    track_height?: number;
    movie_duration?: number;
  }

  export interface MP4Info {
    duration: number;
    timescale: number;
    isFragmented: boolean;
    videoTracks: MP4MediaTrack[];
    audioTracks: MP4MediaTrack[];
  }

  export interface MP4Sample {
    number: number;
    track_id: number;
    timescale: number;
    dts: number;
    cts: number;
    duration: number;
    is_sync: boolean;
    data: Uint8Array;
    size: number;
  }

  export class DataStream {
    constructor(arrayBuffer?: ArrayBuffer, byteOffset?: number, endianness?: boolean);
    buffer: ArrayBuffer;
    getPosition(): number;
    writeUint8(value: number): void;
    writeUint16(value: number): void;
    writeUint32(value: number): void;
    writeInt32(value: number): void;
    writeString(value: string): void;
  }

  export interface MP4Box {
    write(stream: DataStream): void;
  }

  export interface MP4Descriptor {
    tag: number;
    data?: Uint8Array;
    descs?: MP4Descriptor[];
  }

  export interface MP4EsdsBox extends MP4Box {
    esd?: MP4Descriptor;
  }

  export interface MP4ArrayBuffer extends ArrayBuffer {
    fileStart: number;
  }

  export interface MP4File {
    onReady?: (info: MP4Info) => void;
    onError?: (module: string, error: string) => void;
    onSamples?: (trackId: number, user: unknown, samples: MP4Sample[]) => void;
    appendBuffer(buffer: MP4ArrayBuffer): number;
    flush(): void;
    start(): void;
    stop(): void;
    setExtractionOptions(trackId: number, user?: unknown, options?: {
      nbSamples?: number;
      rapAlignement?: boolean;
    }): void;
    getTrackById(trackId: number): {
      edts?: {
        elst?: {
          entries: {
            segment_duration: number;
            media_time: number;
            media_rate_integer: number;
            media_rate_fraction: number;
          }[];
        };
      };
      mdia: {
        minf: {
          stbl: {
            stsd: {
              entries: {
                avcC?: MP4Box;
                hvcC?: MP4Box;
                vpcC?: MP4Box;
                av1C?: MP4Box;
                esds?: MP4EsdsBox;
                data?: { buffer: ArrayBuffer };
              }[];
            };
          };
        };
      };
    } | undefined;
  }

  export function createFile(keepMdatData?: boolean): MP4File;
}
