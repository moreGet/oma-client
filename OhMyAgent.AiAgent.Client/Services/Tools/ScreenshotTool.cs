using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services.Tools;

public sealed class ScreenshotTool : ITool
{
    private static readonly JsonElement Schema = ToolSchemas.Parse(
        """
        {"type":"object","properties":{}}
        """);

    public string Name => "screenshot";
    public string Description => "Capture the primary screen and return it as a base64-encoded PNG (with width/height). The base64 payload may be large; it is intended as a vision input for the server.";
    public JsonElement ParametersSchema => Schema;
    public ToolRisk Risk => ToolRisk.ReadOnly;

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            // System.Drawing.Common 패키지 가정 없이 WinForms(UseWindowsForms=true)가 제공하는
            // System.Drawing 만 사용. 주 화면 경계는 SystemInformation 으로 획득.
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            var primary = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? bounds;

            using var bitmap = new Bitmap(primary.Width, primary.Height, PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(primary.X, primary.Y, 0, 0, primary.Size, CopyPixelOperation.SourceCopy);
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            // ToArray() 는 PNG 를 한 벌 더 복사한다 — 4K 화면이면 수 MB 가 헛돈다.
            // 내부 버퍼를 그대로 인코딩한다(확장 가능 MemoryStream 이라 GetBuffer 사용 가능).
            var base64 = Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);

            return Task.FromResult(ToolResult.Json(new
            {
                format = "png",
                width = primary.Width,
                height = primary.Height,
                base64
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"화면 캡처 실패: {ex.Message}"));
        }
    }
}
