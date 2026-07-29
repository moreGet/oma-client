using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// "폴더 하나에 레코드 하나씩 JSON 파일로" 두는 로컬 저장소.
///
/// <see cref="ChatHistoryService"/>(세션)와 <see cref="ProjectService"/>(프로젝트)가 같은 방식을
/// 각자 구현하고 있던 것을 한 곳으로 모았다. 저장 방식에 손댈 일이 생기면 두 군데를 똑같이 고쳐야 했고,
/// 실제로 그런 변경이 반복됐다. 이제 원자적 교체·경로 탈출 차단·손상 파일 건너뛰기·직렬 접근이
/// 여기서만 정의된다.
///
/// 직렬화는 <see cref="AgentJson.Options"/> 를 공유한다(와이어와 같은 스키마로 저장).
/// </summary>
/// <typeparam name="T">파일 하나에 담기는 레코드 타입.</typeparam>
/// <param name="directory">레코드 파일이 놓이는 폴더. 없으면 저장할 때 만든다.</param>
/// <param name="logTag"><see cref="AppLog"/> 태그 — 사용하는 서비스 이름을 그대로 쓴다.</param>
/// <param name="label">저장 실패 예외 문구에 들어갈 대상 이름(예: "세션", "프로젝트").</param>
internal sealed class JsonFileStore<T>(string directory, string logTag, string label) where T : class
{
    // 같은 폴더에 대한 읽기·쓰기를 직렬화한다 — 원자적 교체(tmp→Move)가 열거와 겹치지 않게.
    private readonly object _ioLock = new();

    /// <summary>
    /// 폴더의 <c>*.json</c> 을 하나씩 읽어 <paramref name="select"/> 로 투영한다.
    /// 손상·읽기 실패 파일은 로그만 남기고 건너뛴다.
    ///
    /// 파일을 하나 읽어 투영하고 바로 버린다 — 레코드 전체를 동시에 들지 않으므로
    /// 대화가 긴 세션이 많아도 피크 메모리가 파일 수에 비례해 커지지 않는다.
    /// 정렬은 호출자 몫이다(요약 타입마다 기준이 다르다).
    /// </summary>
    public async Task<List<TResult>> ListAsync<TResult>(Func<T, TResult> select, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            lock (_ioLock)
            {
                var results = new List<TResult>();
                if (!Directory.Exists(directory))
                    return results;

                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var fs = File.OpenRead(file);
                        var record = JsonSerializer.Deserialize<T>(fs, AgentJson.Options);
                        if (record is null)
                            continue;

                        results.Add(select(record));
                    }
                    catch (Exception ex)
                    {
                        // 손상 파일은 건너뛴다 — 하나가 깨졌다고 목록 전체가 실패하면 안 된다.
                        AppLog.Warn(logTag, $"skip corrupt '{file}'", ex);
                    }
                }

                return results;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>id 로 레코드 하나를 읽는다. 없거나 손상됐으면 null(예외를 던지지 않는다).</summary>
    public async Task<T?> LoadAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return await Task.Run(() =>
        {
            lock (_ioLock)
            {
                var path = PathFor(id);
                if (!File.Exists(path))
                    return null;
                try
                {
                    using var fs = File.OpenRead(path);
                    return JsonSerializer.Deserialize<T>(fs, AgentJson.Options);
                }
                catch (Exception ex)
                {
                    AppLog.Warn(logTag, $"LoadAsync failed for '{id}'", ex);
                    return null;
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>레코드를 원자적으로 덮어쓴다. 실패하면 <see cref="AgentException"/>.</summary>
    public async Task SaveAsync(string id, T record, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var path = PathFor(id);
                    var tmp  = path + ".tmp";
                    var json = JsonSerializer.Serialize(record, AgentJson.Options);
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);   // 부분 쓰기 방지: 원자적 교체
                }
                catch (Exception ex)
                {
                    AppLog.Warn(logTag, $"SaveAsync failed for '{id}'", ex);
                    throw new AgentException($"{label} 저장 실패: {id}", ex);
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>레코드를 지운다. 없으면 무시하고, 실패해도 예외를 던지지 않는다(로그만).</summary>
    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        await Task.Run(() =>
        {
            lock (_ioLock)
            {
                try
                {
                    var path = PathFor(id);
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    AppLog.Warn(logTag, $"DeleteAsync failed for '{id}'", ex);
                }
            }
        }, ct).ConfigureAwait(false);
    }

    private string PathFor(string id)
    {
        // id 는 호출자(Guid 또는 record.Id)에서 오나, 경로 탈출 방지를 위해 파일명만 사용.
        var safe = Path.GetFileName(id);
        return Path.Combine(directory, safe + ".json");
    }
}
