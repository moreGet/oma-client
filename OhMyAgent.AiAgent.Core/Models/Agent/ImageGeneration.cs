using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OhMyAgent.AiAgent.Client.Models;

/// <summary>
/// 이미지 생성 요청 본문 — POST /api/v1/images/generations.
///
/// 클라이언트는 이미지 모델을 직접 호출하지 않는다(모델 키를 앱에 두지 않기 위해) —
/// 사내 서버가 대리 호출하고 base64 만 돌려준다. 그래서 이 계약에는 provider/model/key 필드가 없다.
/// 필드명은 서버 계약 그대로 snake_case 로 고정한다(AgentJson.Options 는 Web 기본이라 camelCase 가 되므로
/// 속성마다 JsonPropertyName 을 명시한다 — 빠지면 서버가 못 읽는다).
/// </summary>
public sealed record ImageGenerationRequest(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("size")]   string Size,
    [property: JsonPropertyName("count")]  int Count,
    [property: JsonPropertyName("format")] string Format);

/// <summary>
/// 생성된 이미지 1장. <c>b64</c> 는 이미지 원본 바이트의 base64(데이터 URI 접두사 없음).
/// <c>revised_prompt</c> 는 모델이 프롬프트를 재작성한 경우에만 온다(없을 수 있다).
/// </summary>
public sealed record GeneratedImage(
    [property: JsonPropertyName("b64")]            string? Base64,
    [property: JsonPropertyName("revised_prompt")] string? RevisedPrompt);

/// <summary>
/// 이미지 생성 응답. <c>usage</c> 는 서버가 쿼터 회계를 위해 붙이지만 클라이언트는 해석하지 않는다 —
/// 도구 결과로 모델에게 돌려줄 값이 아니고(토큰만 먹는다), 쿼터 표시는 /me/quota 가 담당한다.
/// </summary>
public sealed record ImageGenerationResponse(
    [property: JsonPropertyName("images")] IReadOnlyList<GeneratedImage>? Images);
