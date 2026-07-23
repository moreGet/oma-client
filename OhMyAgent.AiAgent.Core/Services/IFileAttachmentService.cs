using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>컴포저 첨부 — 로컬 파일 메타 생성(실제) + 서버 전송 페이로드(미래 stub) (D).</summary>
public interface IFileAttachmentService
{
    /// <summary>로컬 파일 경로로부터 Attachment 메타 생성(크기·MIME 추정). 파일 없으면 AgentException.</summary>
    Attachment CreateFromPath(string filePath);

    /// <summary>[#7 서버측·미래] 첨부의 바이트를 base64로 인코딩해 전송 페이로드 준비. 현재 stub(미연결).</summary>
    Task<string> ReadAsBase64Async(Attachment attachment, CancellationToken ct = default);
}
