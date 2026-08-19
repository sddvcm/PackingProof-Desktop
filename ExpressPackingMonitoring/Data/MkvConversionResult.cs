namespace ExpressPackingMonitoring.Data
{
    public sealed class MkvConversionResult
    {
        public bool Success { get; init; }
        public string FilePath { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
        public bool AlreadyConverted { get; init; }
        // 失败时的 ffmpeg stderr 节选，便于日志/UI 查看具体失败原因
        public string StderrSnippet { get; init; } = "";

        public static MkvConversionResult Ok(string filePath, bool alreadyConverted = false) =>
            new() { Success = true, FilePath = filePath, AlreadyConverted = alreadyConverted };

        public static MkvConversionResult Fail(string errorMessage, string filePath = "", string stderrSnippet = "") =>
            new() { Success = false, ErrorMessage = errorMessage, FilePath = filePath, StderrSnippet = stderrSnippet ?? "" };
    }
}
