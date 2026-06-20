using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// 첨부 서비스 — 클라이언트 실제 메타 변환 + 전송 base64 stub (D).
/// 파일 선택(OpenFileDialog) 자체는 View 코드비하인드 책임. 서비스는 경로→메타 변환만 담당.
/// </summary>
public sealed class FileAttachmentService : IFileAttachmentService
{
    public Attachment CreateFromPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new AgentException("첨부 파일 경로가 비어 있습니다.");

        FileInfo fi;
        try
        {
            fi = new FileInfo(filePath);
        }
        catch (Exception ex)
        {
            throw new AgentException($"첨부 파일 경로가 올바르지 않습니다: {filePath}", ex);
        }

        if (!fi.Exists)
            throw new AgentException($"첨부 파일을 찾을 수 없습니다: {filePath}");

        return new Attachment
        {
            FilePath = fi.FullName,
            FileName = fi.Name,
            SizeBytes = fi.Length,
            ContentType = GuessContentType(fi.Extension)
        };
    }

    /// <summary>
    /// [stub] 서버 첨부 전송은 API_CONTRACT §8.2 계약 확정 후 구현한다.
    /// 현재 전송 경로가 미연결이므로 호출되지 않는다.
    /// </summary>
    public Task<string> ReadAsBase64Async(Attachment attachment, CancellationToken ct = default)
        => throw new NotImplementedException("서버 첨부 전송은 API_CONTRACT §8.2 계약 확정 후 구현");

    /// <summary>확장자 → MIME 간이 매핑. 미지정 시 null.</summary>
    private static string? GuessContentType(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".txt" or ".log" or ".md" => "text/plain",
            ".json"                    => "application/json",
            ".xml"                     => "application/xml",
            ".csv"                     => "text/csv",
            ".html" or ".htm"          => "text/html",
            ".pdf"                     => "application/pdf",
            ".png"                     => "image/png",
            ".jpg" or ".jpeg"          => "image/jpeg",
            ".gif"                     => "image/gif",
            ".bmp"                     => "image/bmp",
            ".webp"                    => "image/webp",
            ".zip"                     => "application/zip",
            ".cs"                      => "text/x-csharp",
            ".js"                      => "text/javascript",
            ".ts"                      => "application/typescript",
            ".py"                      => "text/x-python",
            _                          => null
        };
}
