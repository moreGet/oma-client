using System;
using System.Text.Json;
using OhMyAgent.AiAgent.Client.Models;
using OhMyAgent.AiAgent.Client.Services;
using Xunit;

namespace OhMyAgent.AiAgent.Client.Tests;

/// <summary>
/// 인증 토큰의 저장 경계. 핵심 계약은 하나다: <b>평문 토큰이 디스크로 나가지 않는다.</b>
/// 런타임 표면(AppSettings.AuthToken)은 평문 그대로 유지해 앱 코드가 영향을 받지 않는다.
/// </summary>
public class SettingsTokenTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEifQ.sig";

    // SettingsService 의 디스크 직렬화 옵션과 동일해야 의미 있는 검증이 된다.
    private static string Serialize(AppSettings s) => JsonSerializer.Serialize(s, SettingsService.PersistenceOptions);
    private static AppSettings Deserialize(string json) => JsonSerializer.Deserialize<AppSettings>(json, SettingsService.PersistenceOptions)!;

    [Fact]
    public void PlaintextToken_NeverReachesDisk()
    {
        var settings = new AppSettings { AuthToken = Token };
        settings.AuthTokenProtected = TokenProtector.Protect(settings.AuthToken);

        var json = Serialize(settings);

        Assert.DoesNotContain(Token, json);                 // 토큰 값 자체가 없어야 하고
        Assert.DoesNotContain("\"AuthToken\":", json);      // 평문 키도 사라져야 한다
        Assert.Contains("AuthTokenProtected", json);
    }

    [Fact]
    public void ProtectedToken_RoundTrips()
    {
        var saved = new AppSettings { AuthToken = Token };
        saved.AuthTokenProtected = TokenProtector.Protect(saved.AuthToken);

        var loaded = Deserialize(Serialize(saved));
        loaded.AuthToken = TokenProtector.Unprotect(loaded.AuthTokenProtected);

        Assert.Equal(Token, loaded.AuthToken);
    }

    [Fact]
    public void LegacyPlaintextFile_IsReadableForMigration()
    {
        // v5 이하 파일 형태 — 평문 AuthToken.
        var legacyJson = $$"""
            {"SchemaVersion":5,"AuthToken":"{{Token}}","ServerBaseUrl":"http://localhost:8080"}
            """;

        var loaded = Deserialize(legacyJson);

        // 마이그레이션이 읽을 수 있어야 한다(못 읽으면 사용자가 조용히 로그아웃된다).
        Assert.Equal(Token, loaded.LegacyAuthToken);
        Assert.Equal(string.Empty, loaded.AuthToken);   // 평문 필드는 [JsonIgnore] 라 채워지지 않는다
    }

    [Fact]
    public void AfterMigration_LegacyKeyDisappears()
    {
        var loaded = Deserialize($$"""{"SchemaVersion":5,"AuthToken":"{{Token}}"}""");

        // SettingsService.LoadAsync 의 v5->v6 블록이 하는 일을 그대로 재현.
        loaded.AuthToken = loaded.LegacyAuthToken!;
        loaded.LegacyAuthToken = null;
        loaded.SchemaVersion = 6;
        loaded.AuthTokenProtected = TokenProtector.Protect(loaded.AuthToken);

        var json = Serialize(loaded);

        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain("\"AuthToken\":", json);
        Assert.Equal(6, Deserialize(json).SchemaVersion);
    }

    [Fact]
    public void EmptyToken_WritesNoProtectedField()
    {
        var settings = new AppSettings { AuthToken = "" };
        settings.AuthTokenProtected = TokenProtector.Protect(settings.AuthToken);

        var json = Serialize(settings);

        // null 은 기록하지 않는다 — 빈 값으로 키를 남길 이유가 없다.
        Assert.DoesNotContain("AuthTokenProtected", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("aGVsbG8=")]      // 유효 base64 지만 DPAPI blob 이 아님
    public void Unprotect_ReturnsEmptyOnBadInput(string? bad)
    {
        // 복호화 실패는 예외가 아니라 재로그인으로 이어져야 한다(다른 PC 의 설정 파일 등).
        Assert.Equal(string.Empty, TokenProtector.Unprotect(bad));
    }

    [Fact]
    public void Protect_ReturnsNullForEmptyInput()
    {
        Assert.Null(TokenProtector.Protect(""));
        Assert.Null(TokenProtector.Protect(null));
    }

    [Fact]
    public void Ciphertext_DiffersFromPlaintextAndIsBase64()
    {
        var cipher = TokenProtector.Protect(Token)!;

        Assert.NotEqual(Token, cipher);
        Assert.True(Convert.TryFromBase64String(cipher, new byte[cipher.Length], out _));
    }
}
