namespace Clip.Core;

public sealed record NvidiaEncoderProbeResult(int ExitCode, string StandardError)
{
    public bool IsAvailable => ExitCode == 0;

    public string Summary
    {
        get
        {
            if (IsAvailable) return "NVENC 编码检测通过。";
            if (Has("Driver does not support the required nvenc API version") || Has("CUDA_ERROR_SYSTEM_DRIVER_MISMATCH"))
                return "当前 FFmpeg 与 NVIDIA 驱动不兼容。请更新驱动，或选择与现有驱动兼容的 FFmpeg。";
            if (Has("Unknown encoder") && Has("h264_nvenc"))
                return "当前 FFmpeg 未包含 h264_nvenc 编码器，请选择支持 NVENC 的 FFmpeg。";
            if (Has("Unrecognized option"))
                return "当前 FFmpeg 不支持 NVENC 检测所需参数，请选择兼容的 FFmpeg。";
            if (Has("Cannot load") && (Has("nvcuda") || Has("libcuda") || Has("nvEncodeAPI") || Has("libnvidia-encode")))
                return "无法加载 NVIDIA 驱动组件，请检查驱动安装；具体原因见检测详情。";
            if (Has("No capable devices found") || Has("No CUDA capable devices found"))
                return "当前 FFmpeg 未找到可用的 NVENC 设备，请检查显卡支持、驱动及检测详情。";
            return "NVENC 试编码失败，请查看检测详情中的 FFmpeg 错误。";
        }
    }

    private bool Has(string text) => StandardError.Contains(text, StringComparison.OrdinalIgnoreCase);
}
