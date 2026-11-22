using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ZJUSS.Core.Access;
using ZJUSS.Core.Adapters.Shared;
using ZJUSS.Core.Classroom;
using ZJUSS.Core.Logging;
using ZJUSS.Core.Messages;
using ZJUSS.Core.Models;

namespace ZJUSS.Core.Adapters.Classroom;

/// <summary>
/// 智云课堂只读采集适配器：统一身份认证 → 学期/周课表 → 语音文字/课件幻灯片。
/// 会话与 token 只保留在内存；不写本机文件、不重试（会话失效只重新登录一次）、不匹配课程。
/// 任何情况下都不把 token、Cookie、账号、完整文字或 PPT 字节写入日志或结果说明。
/// </summary>
public sealed class ClassroomSourceAdapter : IClassroomSource
{
    /// <summary>一次课程列表刷新最多读取的学期数；超过按结构异常处理，避免无上限抓取。</summary>
    internal const int MaxTerms = 100;

    internal const int MaxPptPages = 500;

    internal const long MaxPptBytes = 200L * 1024 * 1024;

    private const string Module = "M12";

    private static readonly IReadOnlyDictionary<string, LessonAccess> NoLessons =
        new Dictionary<string, LessonAccess>(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly ICredentialProvider _credentialProvider;
    private readonly IAppLogger _logger;
    private readonly TimeZoneInfo _timeZone;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private CasLoginClient? _login;
    private string? _token;
    private IReadOnlyDictionary<string, LessonAccess> _lessonAccess = NoLessons;

    public ClassroomSourceAdapter(
        HttpClient httpClient,
        ICredentialProvider credentials,
        IAppLogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialProvider = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _logger = logger ?? NullAppLogger.Instance;
        _timeZone = timeZone ?? timeProvider?.LocalTimeZone ?? TimeZoneInfo.Local;
    }

    public async Task<ClassroomSourceResult<IReadOnlyList<ClassroomCourse>>> GetCoursesAsync(
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var catalog = await LoadCatalogAsync("Courses", cancellationToken).ConfigureAwait(false);
            LogSuccess(
                "Courses",
                ClassroomContentKind.CourseList,
                catalog.Courses.Count,
                stopwatch);
            return ClassroomSourceResult<IReadOnlyList<ClassroomCourse>>.Ok(catalog.Courses);
        }
        catch (Exception exception)
        {
            return FailureResult<IReadOnlyList<ClassroomCourse>>(
                "Courses",
                ClassroomContentKind.CourseList,
                exception,
                stopwatch,
                cancellationToken);
        }
    }

    public async Task<ClassroomSourceResult<IReadOnlyList<ClassroomLesson>>> GetLessonsAsync(
        string classroomCourseId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(classroomCourseId))
            {
                throw CreateFailure(ClassroomFailureReason.NotFound, "缺少课堂课程标识。");
            }

            var catalog = await LoadCatalogAsync("Lessons", cancellationToken).ConfigureAwait(false);
            if (!catalog.LessonsByCourse.TryGetValue(classroomCourseId, out var lessons))
            {
                throw CreateFailure(ClassroomFailureReason.NotFound, "平台当前清单中没有这门课程。");
            }

            LogSuccess(
                "Lessons",
                ClassroomContentKind.LessonList,
                lessons.Count,
                stopwatch,
                classroomCourseId);
            return ClassroomSourceResult<IReadOnlyList<ClassroomLesson>>.Ok(lessons);
        }
        catch (Exception exception)
        {
            return FailureResult<IReadOnlyList<ClassroomLesson>>(
                "Lessons",
                ClassroomContentKind.LessonList,
                exception,
                stopwatch,
                cancellationToken,
                classroomCourseId);
        }
    }

    public async Task<ClassroomSourceResult<string>> GetTranscriptAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(lessonId))
            {
                throw CreateFailure(ClassroomFailureReason.NotFound, "缺少课节标识。");
            }

            await EnsureSessionAsync(forceLogin: false, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var access = GetLessonAccess(lessonId);
            if (access is { IsApply: true })
            {
                throw CreateFailure(
                    ClassroomFailureReason.NoPermission,
                    "这一节需要在智云课堂申请后才能读取。");
            }

            if (access is { IsReplay: false })
            {
                // 直播中/未开始：平台没有语音文字，按“暂无”返回，不当作错误。
                LogSuccess(
                    "Transcript",
                    ClassroomContentKind.Transcript,
                    0,
                    stopwatch,
                    classroomCourseId,
                    lessonId);
                return ClassroomSourceResult<string>.Ok(string.Empty);
            }

            var text = await WithSessionAsync(
                session => FetchTranscriptAsync(session, lessonId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            LogSuccess(
                "Transcript",
                ClassroomContentKind.Transcript,
                null,
                stopwatch,
                classroomCourseId,
                lessonId);
            return ClassroomSourceResult<string>.Ok(text);
        }
        catch (Exception exception)
        {
            return FailureResult<string>(
                "Transcript",
                ClassroomContentKind.Transcript,
                exception,
                stopwatch,
                cancellationToken,
                classroomCourseId,
                lessonId);
        }
    }

    public async Task<ClassroomSourceResult<IReadOnlyList<ClassroomPptFile>>> GetPptInfoAsync(
        string classroomCourseId,
        string lessonId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(classroomCourseId) || string.IsNullOrWhiteSpace(lessonId))
            {
                throw CreateFailure(ClassroomFailureReason.NotFound, "缺少课程或课节标识。");
            }

            await EnsureSessionAsync(forceLogin: false, cancellationToken).ConfigureAwait(false);
            var access = GetLessonAccess(lessonId);
            if (access is { IsApply: true })
            {
                throw CreateFailure(
                    ClassroomFailureReason.NoPermission,
                    "这一节需要在智云课堂申请后才能读取课件。");
            }

            if (access is { IsReplay: false })
            {
                return ClassroomSourceResult<IReadOnlyList<ClassroomPptFile>>.Ok(
                    Array.Empty<ClassroomPptFile>());
            }

            var sources = await WithSessionAsync(
                session => FetchPptSourcesAsync(session, classroomCourseId, lessonId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ClassroomPptFile> files = sources.Count == 0
                ? Array.Empty<ClassroomPptFile>()
                : new[]
                {
                    new ClassroomPptFile
                    {
                        FileId = "pdf",
                        OriginalFileName = "课件.pdf",
                        PageCount = sources.Count,
                        Status = ClassroomContentStatus.NotRead
                    }
                };

            LogSuccess(
                "PptInfo",
                ClassroomContentKind.Ppt,
                sources.Count,
                stopwatch,
                classroomCourseId,
                lessonId);
            return ClassroomSourceResult<IReadOnlyList<ClassroomPptFile>>.Ok(files);
        }
        catch (Exception exception)
        {
            return FailureResult<IReadOnlyList<ClassroomPptFile>>(
                "PptInfo",
                ClassroomContentKind.Ppt,
                exception,
                stopwatch,
                cancellationToken,
                classroomCourseId,
                lessonId);
        }
    }

    public async Task<ClassroomSourceResult<ClassroomPptDownload>> DownloadPptAsync(
        string classroomCourseId,
        string lessonId,
        ClassroomPptFile file,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(classroomCourseId) || string.IsNullOrWhiteSpace(lessonId))
            {
                throw CreateFailure(ClassroomFailureReason.NotFound, "缺少课程或课节标识。");
            }

            if (file is null || !IsPptPlaceholder(file))
            {
                throw CreateFailure(
                    ClassroomFailureReason.NotFound,
                    "这一节没有平台提供的课件 PDF 占位。");
            }

            await EnsureSessionAsync(forceLogin: false, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var access = GetLessonAccess(lessonId);
            if (access is { IsApply: true })
            {
                throw CreateFailure(
                    ClassroomFailureReason.NoPermission,
                    "这一节需要在智云课堂申请后才能读取课件。");
            }

            if (access is { IsReplay: false })
            {
                throw CreateFailure(ClassroomFailureReason.NotFound, "这一节还没有可下载的课件。");
            }

            var images = await WithSessionAsync(
                session => FetchPptImagesAsync(session, classroomCourseId, lessonId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] pdf;
            try
            {
                pdf = CoursewarePdfBuilder.Build(images);
            }
            catch (CoursewarePdfException exception) when (exception.Error == CoursewarePdfError.NoImages)
            {
                // 平台确认这一节没有课件页：属于“暂无”，不是页面改版。
                throw CreateFailure(ClassroomFailureReason.NotFound, "这一节没有课件。");
            }
            catch (CoursewarePdfException)
            {
                throw CreateFailure(ClassroomFailureReason.StorageFailed, "课件图片无法合成为 PDF。");
            }

            LogSuccess(
                "Ppt",
                ClassroomContentKind.Ppt,
                images.Count,
                stopwatch,
                classroomCourseId,
                lessonId);
            return ClassroomSourceResult<ClassroomPptDownload>.Ok(new ClassroomPptDownload
            {
                OriginalFileName = "课件.pdf",
                Content = new MemoryStream(pdf, writable: false),
                SizeBytes = pdf.Length
            });
        }
        catch (Exception exception)
        {
            return FailureResult<ClassroomPptDownload>(
                "Ppt",
                ClassroomContentKind.Ppt,
                exception,
                stopwatch,
                cancellationToken,
                classroomCourseId,
                lessonId);
        }
    }

    private async Task<ClassroomCatalog> LoadCatalogAsync(
        string phase,
        CancellationToken cancellationToken)
    {
        var catalog = await WithSessionAsync(
            session => FetchCatalogAsync(session, phase, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        _lessonAccess = catalog.Access;
        return catalog;
    }

    private async Task<ClassroomCatalog> FetchCatalogAsync(
        Session session,
        string phase,
        CancellationToken cancellationToken)
    {
        var termBody = await SendWithTokenQueryAsync(
            ClassroomEndpoints.TermYears,
            phase,
            cancellationToken).ConfigureAwait(false);
        var terms = ClassroomJsonParser.ParseTerms(termBody, phase);
        if (terms.Count == 0)
        {
            return ClassroomCatalog.Empty;
        }

        if (terms.Count > MaxTerms)
        {
            _logger.Warn(Module, $"阶段={phase} 学期数={terms.Count} 超过上限={MaxTerms}");
            throw CreateFailure(
                ClassroomFailureReason.SchemaChanged,
                "平台返回的学期数量超过读取上限。");
        }

        var jwtUserId = ClassroomJsonParser.TryReadJwtUserId(session.Token);
        var termUserId = terms
            .Select(term => term.UserId)
            .FirstOrDefault(userId => !string.IsNullOrWhiteSpace(userId));
        var profileUserId = await TryReadProfileUserIdAsync(session, phase, cancellationToken)
            .ConfigureAwait(false);
        var candidateUserIds = new[] { termUserId, jwtUserId, profileUserId }
            .Where(userId => !string.IsNullOrWhiteSpace(userId))
            .Select(userId => userId!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidateUserIds.Count == 0)
        {
            throw CreateFailure(ClassroomFailureReason.SchemaChanged, "缺少周课表所需的 user_id。");
        }

        var termCourses = new List<TermCourses>(terms.Count);
        var skippedTerms = 0;
        var primaryTerm = terms.MaxBy(term => term.EndDate);
        string? resolvedUserId = null;
        foreach (var term in terms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = resolvedUserId is null
                ? candidateUserIds
                : new List<string> { resolvedUserId };
            IReadOnlyList<ClassroomWeekCourse> chosen = Array.Empty<ClassroomWeekCourse>();
            var anySuccess = false;
            Exception? lastFailure = null;
            foreach (var userId in candidates)
            {
                try
                {
                    var courses = await FetchTermCoursesAsync(
                        session,
                        term,
                        userId,
                        phase,
                        cancellationToken).ConfigureAwait(false);
                    anySuccess = true;
                    if (courses.Count > 0)
                    {
                        chosen = courses;
                        resolvedUserId ??= userId;
                        break;
                    }
                }
                catch (Exception exception) when (IsSkippableHistoricalTermFailure(exception))
                {
                    lastFailure = exception;
                }
            }

            if (ReferenceEquals(term, primaryTerm) && chosen.Count == 0)
            {
                // get-week-schedules 在部分账号上会以错误/缺少的 user_id 返回空列表；
                // get-my-course-month 只依赖 Bearer token，是当前用户课程的可靠回退。
                try
                {
                    var monthly = await FetchTermFromMonthAsync(
                        session,
                        term,
                        phase,
                        cancellationToken).ConfigureAwait(false);
                    if (monthly.Count > 0)
                    {
                        chosen = monthly;
                    }
                }
                catch (Exception monthlyFailure) when (
                    IsSkippableHistoricalTermFailure(monthlyFailure))
                {
                    var monthly = Classify(monthlyFailure, cancellationToken);
                    _logger.Warn(Module, $"阶段={phase} 主学期={term.Term} 月历回退失败 类别={monthly.Reason} 原因={monthly.Detail}");
                }
            }

            if (!anySuccess && lastFailure is not null && chosen.Count == 0)
            {
                if (ReferenceEquals(term, primaryTerm))
                {
                    var primaryFailure = Classify(lastFailure, cancellationToken);
                    _logger.Warn(Module, $"阶段={phase} 主学期={term.Term} 读取失败 类别={primaryFailure.Reason} 原因={primaryFailure.Detail}");
                    throw lastFailure;
                }

                skippedTerms++;
                var skipped = Classify(lastFailure, cancellationToken);
                _logger.Warn(Module, $"阶段={phase} 学期={term.Term} 跳过 类别={skipped.Reason} 原因={skipped.Detail}");
                continue;
            }

            termCourses.Add(new TermCourses(term, chosen));
            if (ReferenceEquals(term, primaryTerm) && chosen.Count == 0)
            {
                _logger.Warn(
                    Module,
                    $"阶段={phase} 主学期={term.Term} 未返回课程 候选标识数={candidateUserIds.Count} " +
                    $"token标识={(jwtUserId is null ? 0 : 1)} 字典标识={(termUserId is null ? 0 : 1)} 资料标识={(profileUserId is null ? 0 : 1)}");
            }
        }

        if (termCourses.Count == 0 && skippedTerms > 0)
        {
            throw CreateFailure(
                ClassroomFailureReason.SchemaChanged,
                "平台学期字典中的所有课表都无法读取。");
        }

        return BuildCatalog(termCourses);
    }

    private static bool IsSkippableHistoricalTermFailure(Exception exception) => exception switch
    {
        ClassroomSourceFailureException failure => failure.Reason is
            ClassroomFailureReason.NotFound or
            ClassroomFailureReason.SchemaChanged or
            ClassroomFailureReason.NoPermission,
        AdapterFailureException failure => failure.Reason is
            SourceFailureReason.SchemaChanged or
            SourceFailureReason.Maintenance,
        _ => false
    };

    private async Task<string> FetchTranscriptAsync(
        Session session,
        string lessonId,
        CancellationToken cancellationToken)
    {
        var first = await SendRawAsync(
            ClassroomEndpoints.Transcript(lessonId),
            cancellationToken).ConfigureAwait(false);
        if (first.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var second = await SendRawAsync(
                ClassroomEndpoints.Transcript(lessonId, session.Token),
                cancellationToken).ConfigureAwait(false);
            EnsureDataResponse(second.Status, second.Body, "Transcript");
            return ClassroomJsonParser.ParseTranscript(second.Body, "Transcript");
        }

        EnsureDataResponse(first.Status, first.Body, "Transcript");
        return ClassroomJsonParser.ParseTranscript(first.Body, "Transcript");
    }

    private async Task<IReadOnlyList<ClassroomPptImageSource>> FetchPptSourcesAsync(
        Session session,
        string courseId,
        string lessonId,
        CancellationToken cancellationToken)
    {
        var allEntries = new List<ClassroomPptImageSource>();
        var page = 1;
        while (page <= 10)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                ClassroomEndpoints.SearchPpt(courseId, lessonId, page));
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.Token);
            var response = await session.Login
                .SendRawAsync(request, cancellationToken)
                .ConfigureAwait(false);
            EnsureDataResponse(response.StatusCode, response.Body, "Ppt");
            var body = response.Body;

            var pageEntries = ClassroomJsonParser.ParsePptList(body, "Ppt");
            allEntries.AddRange(pageEntries);
            var total = ClassroomJsonParser.TryReadPptTotal(body);
            if (pageEntries.Count == 0 ||
                (total is { } totalCount && allEntries.Count >= totalCount))
            {
                break;
            }

            page++;
        }

        return allEntries
            .GroupBy(entry => entry.Url, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(entry => entry.CreatedSec)
            .ToArray();
    }

    private async Task<IReadOnlyList<byte[]>> FetchPptImagesAsync(
        Session session,
        string courseId,
        string lessonId,
        CancellationToken cancellationToken)
    {
        var entries = await FetchPptSourcesAsync(session, courseId, lessonId, cancellationToken)
            .ConfigureAwait(false);
        if (entries.Count == 0)
        {
            throw CreateFailure(ClassroomFailureReason.NotFound, "这一节没有课件。");
        }

        if (entries.Count > MaxPptPages)
        {
            throw CreateFailure(
                ClassroomFailureReason.SchemaChanged,
                "课件页数超过上限，已停止下载。");
        }

        var images = new List<byte[]>(entries.Count);
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = await DownloadImageAsync(session, entry.Url, cancellationToken)
                .ConfigureAwait(false);
            totalBytes += image.Length;
            if (totalBytes > MaxPptBytes)
            {
                throw CreateFailure(
                    ClassroomFailureReason.StorageFailed,
                    "课件总大小超过上限，已停止下载。");
            }

            images.Add(image);
        }

        return images;
    }

    private async Task<byte[]> DownloadImageAsync(
        Session session,
        string source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw CreateFailure(ClassroomFailureReason.SchemaChanged, "课件图片地址为空。");
        }

        var value = source.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUri(value);
        }

        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("//", StringComparison.Ordinal) &&
            !value.StartsWith("/", StringComparison.Ordinal))
        {
            // 平台没有提供可识别地址时，最后尝试把纯 base64 当作单页图片。
            return DecodeBase64(value);
        }

        var uri = value.StartsWith("//", StringComparison.Ordinal)
            ? new Uri("https:" + value, UriKind.Absolute)
            : value.StartsWith("/", StringComparison.Ordinal)
                ? new Uri(ClassroomEndpoints.ClassroomRoot, value)
                : new Uri(value, UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "image/*");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session.Token);

        var (status, bytes) = await session.Login
            .SendBytesAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw CreateFailure(ClassroomFailureReason.NoPermission, "课件图片没有访问权限。");
        }

        if (status == HttpStatusCode.NotFound)
        {
            throw CreateFailure(ClassroomFailureReason.NotFound, "课件图片不存在。");
        }

        if ((int)status >= 500)
        {
            throw CreateFailure(ClassroomFailureReason.Unreachable, "课件图片服务暂时不可用。");
        }

        if ((int)status is < 200 or >= 300)
        {
            throw CreateFailure(ClassroomFailureReason.SchemaChanged, "课件图片返回了非预期状态。");
        }

        if (bytes.Length > MaxPptBytes)
        {
            throw CreateFailure(ClassroomFailureReason.StorageFailed, "课件图片超过大小上限。");
        }

        if (bytes.Length == 0)
        {
            throw CreateFailure(ClassroomFailureReason.SchemaChanged, "课件图片内容为空。");
        }

        return bytes;
    }

    private static byte[] DecodeDataUri(string value)
    {
        var comma = value.IndexOf(',');
        if (comma < 0)
        {
            throw CreateFailure(ClassroomFailureReason.SchemaChanged, "data URI 格式不正确。");
        }

        var metadata = value.AsSpan(5, comma - 5);
        if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            throw CreateFailure(ClassroomFailureReason.SchemaChanged, "仅支持 base64 的 data URI 图片。");
        }

        return DecodeBase64(value[(comma + 1)..]);
    }

    private static byte[] DecodeBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 0)
            {
                throw CreateFailure(ClassroomFailureReason.SchemaChanged, "base64 图片内容为空。");
            }

            return bytes;
        }
        catch (FormatException)
        {
            throw CreateFailure(ClassroomFailureReason.SchemaChanged, "课件图片地址无法识别。");
        }
    }

    private async Task<string> SendWithTokenQueryAsync(
        Uri uri,
        string phase,
        CancellationToken cancellationToken)
    {
        var (status, body) = await SendRawAsync(uri, cancellationToken).ConfigureAwait(false);
        EnsureDataResponse(status, body, phase);
        return body;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendRawAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return (response.StatusCode, body);
    }

    private static void EnsureDataResponse(
        HttpStatusCode status,
        string body,
        string phase)
    {
        if ((int)status >= 500)
        {
            throw CreateFailure(ClassroomFailureReason.Unreachable, "学校网站暂时无法访问。");
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw CreateFailure(
                ClassroomFailureReason.LoginFailed,
                "读取时登录状态失效。",
                sessionExpired: true);
        }

        if ((int)status is < 200 or >= 300)
        {
            throw CreateFailure(ClassroomFailureReason.SchemaChanged, "读取返回了非预期状态。");
        }

        if (LooksLikeLoginPage(body))
        {
            throw CreateFailure(
                ClassroomFailureReason.SchemaChanged,
                "读取时返回了登录页。",
                sessionExpired: true);
        }
    }

    private static bool LooksLikeLoginPage(string body)
    {
        var trimmed = body.AsSpan().TrimStart();
        return !trimmed.IsEmpty &&
               trimmed[0] == '<' &&
               HtmlScanner.LooksLikeLoginPage(body);
    }

    private async Task<T> WithSessionAsync<T>(
        Func<Session, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var session = await EnsureSessionAsync(forceLogin: false, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await action(session).ConfigureAwait(false);
        }
        catch (ClassroomSourceFailureException exception) when (
            exception.SessionExpired && !cancellationToken.IsCancellationRequested)
        {
            _logger.Warn(Module, "阶段=会话 状态=失效 动作=重新登录一次");
            session = await EnsureSessionAsync(forceLogin: true, cancellationToken)
                .ConfigureAwait(false);
            var result = await action(session).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private async Task<Session> EnsureSessionAsync(
        bool forceLogin,
        CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceLogin && _login is not null && _token is not null)
            {
                return new Session(_login, _token);
            }

            _login = null;
            _token = null;
            var credentials = _credentialProvider.GetCredentials();
            if (credentials is null)
            {
                throw CreateFailure(ClassroomFailureReason.LoginFailed, "没有可用的学校凭据。");
            }

            var (client, status) = await CasLoginClient.LoginWithRetryAsync(
                () => new CasLoginClient(
                    _httpClient,
                    ClassroomEndpoints.ClassroomRoot,
                    _logger,
                    ClassroomEndpoints.CasLoginEntry),
                credentials,
                _logger,
                cancellationToken).ConfigureAwait(false);

            if (status == CasLoginStatus.VerificationRequired)
            {
                throw CreateFailure(
                    ClassroomFailureReason.VerificationRequired,
                    "登录需要验证码或二次验证。");
            }

            if (status != CasLoginStatus.Success)
            {
                throw CreateFailure(ClassroomFailureReason.LoginFailed, "登录失败。");
            }

            using (var rootRequest = new HttpRequestMessage(
                       HttpMethod.Get,
                       ClassroomEndpoints.ClassroomRoot))
            {
                await client.SendReadAsync(rootRequest, "Session", cancellationToken)
                    .ConfigureAwait(false);
            }

            var token = client.GetCookieValue(ClassroomEndpoints.ClassroomRoot, "_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw CreateFailure(ClassroomFailureReason.LoginFailed, "登录后没有取得会话 token。");
            }

            _login = client;
            _token = token;
            return new Session(client, token);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private LessonAccess? GetLessonAccess(string lessonId)
    {
        var access = _lessonAccess;
        return access.TryGetValue(lessonId, out var value) ? value : null;
    }

    private static bool IsPptPlaceholder(ClassroomPptFile file)
    {
        var fileId = file.FileId;
        return !string.IsNullOrEmpty(fileId) &&
               (fileId.Equals("pdf", StringComparison.OrdinalIgnoreCase) ||
                fileId.Equals("images", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> TryReadProfileUserIdAsync(
        Session session,
        string phase,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await SendWithTokenQueryAsync(
                ClassroomEndpoints.UserInfo(session.Token),
                phase,
                cancellationToken).ConfigureAwait(false);
            return ClassroomJsonParser.TryReadUserId(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            IsSkippableHistoricalTermFailure(exception) ||
            exception is ClassroomSourceFailureException or AdapterFailureException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<ClassroomWeekCourse>> FetchTermFromMonthAsync(
        Session session,
        ClassroomTermInfo term,
        string phase,
        CancellationToken cancellationToken)
    {
        var byLesson = new Dictionary<string, ClassroomWeekCourse>(StringComparer.Ordinal);
        var diagnosedMonth = false;
        var cursor = new DateOnly(term.BeginDate.Year, term.BeginDate.Month, 1);
        var last = new DateOnly(term.EndDate.Year, term.EndDate.Month, 1);
        while (cursor <= last)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var month = cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var body = await SendBearerAsync(
                session,
                ClassroomEndpoints.MyCourseMonth(month),
                phase,
                cancellationToken).ConfigureAwait(false);
            var courses = ClassroomJsonParser.ParseMyCourseMonth(body, term, phase);
            if (!diagnosedMonth &&
                courses.Count > 0 &&
                courses.All(course => course.Day is null && course.StartAt is null))
            {
                diagnosedMonth = true;
                _logger.Warn(
                    Module,
                    $"阶段={phase} 月历课节日期字段诊断 {ClassroomJsonParser.DescribeScheduleShape(body, phase)}");
            }

            foreach (var course in courses)
            {
                if (!string.IsNullOrWhiteSpace(course.LessonId) &&
                    !byLesson.ContainsKey(course.LessonId))
                {
                    byLesson[course.LessonId] = course;
                }
            }

            cursor = cursor.AddMonths(1);
        }

        return byLesson.Values
            .OrderBy(course => course.Day ?? "\uFFFF", StringComparer.Ordinal)
            .ThenBy(course => course.StartAt ?? long.MaxValue)
            .ToArray();
    }

    private async Task<string> SendBearerAsync(
        Session session,
        Uri uri,
        string phase,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        var response = await session.Login
            .SendRawAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureDataResponse(response.StatusCode, response.Body, phase);
        return response.Body;
    }

    private async Task<IReadOnlyList<ClassroomWeekCourse>> FetchTermCoursesAsync(
        Session session,
        ClassroomTermInfo term,
        string userId,
        string phase,
        CancellationToken cancellationToken)
    {
        var uri = ClassroomEndpoints.WeekSchedules(
            userId,
            term.BeginDate,
            term.EndDate,
            session.Token);
        var body = await SendWithTokenQueryAsync(uri, phase, cancellationToken).ConfigureAwait(false);
        var courses = ClassroomJsonParser.ParseWeekSchedules(body, term, phase);
        if (courses.Count == 0)
        {
            _logger.Warn(
                Module,
                $"阶段={phase} 周课表未返回课节 结构={ClassroomJsonParser.DescribeScheduleShape(body, phase)}");
        }
        else if (courses.All(course => course.Day is null && course.StartAt is null))
        {
            _logger.Warn(
                Module,
                $"阶段={phase} 周课表课节日期字段诊断 {ClassroomJsonParser.DescribeScheduleShape(body, phase)}");
        }

        return courses;
    }

    private ClassroomCatalog BuildCatalog(IReadOnlyList<TermCourses> termCourses)
    {
        var courseOrder = new List<string>();
        var courseBuilders = new Dictionary<string, CourseBuilder>(StringComparer.Ordinal);
        foreach (var term in termCourses)
        {
            foreach (var record in term.Courses)
            {
                if (!courseBuilders.TryGetValue(record.CourseId, out var builder))
                {
                    builder = new CourseBuilder(new ClassroomCourse
                    {
                        CourseId = record.CourseId,
                        Name = record.CourseName,
                        Term = term.Term.Term,
                        PlatformInfo = record.PlatformInfo
                    });
                    courseBuilders.Add(record.CourseId, builder);
                    courseOrder.Add(record.CourseId);
                }

                if (builder.TryReserve(record.LessonId))
                {
                    var lesson = BuildLesson(record, out var lessonAccess);
                    builder.Add(new LessonEntry(lesson, record.StartAt, lessonAccess));
                }
            }
        }

        var courses = new List<ClassroomCourse>(courseOrder.Count);
        var lessonsByCourse = new Dictionary<string, IReadOnlyList<ClassroomLesson>>(
            courseOrder.Count,
            StringComparer.Ordinal);
        var access = new Dictionary<string, LessonAccess>(StringComparer.Ordinal);
        foreach (var courseId in courseOrder)
        {
            var builder = courseBuilders[courseId];
            courses.Add(builder.Course);
            lessonsByCourse.Add(courseId, builder.BuildLessons());
            foreach (var entry in builder.Entries)
            {
                access[entry.Lesson.LessonId] = entry.Access;
            }
        }

        return new ClassroomCatalog
        {
            Courses = courses,
            LessonsByCourse = lessonsByCourse,
            Access = access
        };
    }

    private ClassroomLesson BuildLesson(ClassroomWeekCourse record, out LessonAccess access)
    {
        var isReplay = !record.IsApply && record.Status == 6;
        access = new LessonAccess(record.IsApply, isReplay);

        IReadOnlyList<ClassroomPptFile> pptFiles = isReplay
            ? new[]
            {
                new ClassroomPptFile
                {
                    FileId = "pdf",
                    OriginalFileName = "课件.pdf",
                    Status = ClassroomContentStatus.NotRead
                }
            }
            : Array.Empty<ClassroomPptFile>();

        bool? transcriptAvailable = record.IsApply
            ? false
            : isReplay
                ? null
                : false;

        return new ClassroomLesson
        {
            LessonId = record.LessonId,
            DateText = string.IsNullOrWhiteSpace(record.Day)
                ? FormatLocalDate(record.StartAt)
                : record.Day.Trim(),
            SessionText = record.SessionText ?? FormatSessionText(record.StartAt, record.EndAt),
            Title = null,
            PptFiles = pptFiles,
            TranscriptAvailable = transcriptAvailable,
            TranscriptStatus = ClassroomContentStatus.NotRead,
            RemovedFromPlatform = false
        };
    }

    private string? FormatSessionText(long? startAt, long? endAt)
    {
        if (startAt is null && endAt is null)
        {
            return null;
        }

        try
        {
            if (startAt is not null && endAt is not null)
            {
                return FormatLocalTime(startAt.Value) + "-" + FormatLocalTime(endAt.Value);
            }

            return FormatLocalTime((startAt ?? endAt)!.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private string FormatLocalTime(long unixSeconds)
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var local = TimeZoneInfo.ConvertTime(instant, _timeZone);
        return local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private string? FormatLocalDate(long? unixSeconds)
    {
        if (unixSeconds is null or <= 0)
        {
            return null;
        }

        try
        {
            var instant = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
            var local = TimeZoneInfo.ConvertTime(instant, _timeZone);
            return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private ClassroomSourceResult<T> FailureResult<T>(
        string phase,
        string contentKind,
        Exception exception,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        string? courseId = null,
        string? lessonId = null)
    {
        var (reason, detail) = Classify(exception, cancellationToken);
        LogFailure(
            phase,
            contentKind,
            exception,
            stopwatch,
            cancellationToken,
            courseId,
            lessonId);
        return ClassroomSourceResult<T>.Fail(reason, detail);
    }

    private static (ClassroomFailureReason Reason, string? Detail) Classify(
        Exception exception,
        CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return (ClassroomFailureReason.Cancelled, null);
            case OperationCanceledException:
                return (ClassroomFailureReason.Timeout, "读取超时。");
            case ClassroomSourceFailureException failure:
                return (failure.Reason, failure.Message);
            case AdapterFailureException failure:
                return (Map(failure.Reason), failure.Message);
            case HttpRequestException:
                return (ClassroomFailureReason.Unreachable, "无法连接学校网站。");
            case JsonException:
                return (ClassroomFailureReason.SchemaChanged, "返回内容格式不符。");
            case CoursewarePdfException pdf when pdf.Error == CoursewarePdfError.NoImages:
                return (ClassroomFailureReason.NotFound, "这一节没有课件。");
            case CoursewarePdfException:
                return (ClassroomFailureReason.StorageFailed, "课件无法合成为 PDF。");
            default:
                return (ClassroomFailureReason.Unknown, null);
        }
    }

    private static ClassroomFailureReason Map(SourceFailureReason reason) => reason switch
    {
        SourceFailureReason.Unreachable => ClassroomFailureReason.Unreachable,
        SourceFailureReason.Timeout => ClassroomFailureReason.Timeout,
        SourceFailureReason.Maintenance => ClassroomFailureReason.Unreachable,
        SourceFailureReason.LoginFailed => ClassroomFailureReason.LoginFailed,
        SourceFailureReason.VerificationRequired => ClassroomFailureReason.VerificationRequired,
        SourceFailureReason.SchemaChanged => ClassroomFailureReason.SchemaChanged,
        SourceFailureReason.DataCorrupted => ClassroomFailureReason.SchemaChanged,
        SourceFailureReason.StorageFailed => ClassroomFailureReason.StorageFailed,
        _ => ClassroomFailureReason.Unknown
    };

    private void LogSuccess(
        string phase,
        string contentKind,
        int? fileCount,
        Stopwatch stopwatch,
        string? courseId = null,
        string? lessonId = null)
    {
        stopwatch.Stop();
        _logger.Info(Module, ClassroomLog.Format(new ClassroomLogRecord
        {
            Phase = phase,
            ClassroomCourseId = courseId,
            LessonId = lessonId,
            ContentKind = contentKind,
            Status = ClassroomContentStatus.Read,
            FileCount = fileCount,
            Elapsed = stopwatch.Elapsed
        }));
    }

    private void LogFailure(
        string phase,
        string contentKind,
        Exception exception,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        string? courseId = null,
        string? lessonId = null)
    {
        stopwatch.Stop();
        var (reason, _) = Classify(exception, cancellationToken);
        var status = reason == ClassroomFailureReason.Cancelled
            ? ClassroomContentStatus.Cancelled
            : ClassroomContentStatus.Failed;
        _logger.Warn(Module, ClassroomLog.Format(new ClassroomLogRecord
        {
            Phase = phase,
            ClassroomCourseId = courseId,
            LessonId = lessonId,
            ContentKind = contentKind,
            Status = status,
            Failure = reason,
            Elapsed = stopwatch.Elapsed
        }));
    }

    private static ClassroomSourceFailureException CreateFailure(
        ClassroomFailureReason reason,
        string message,
        bool sessionExpired = false) =>
        new(reason, message, sessionExpired);

    private sealed record Session(CasLoginClient Login, string Token);

    private readonly record struct LessonAccess(bool IsApply, bool IsReplay);

    private sealed record TermCourses(
        ClassroomTermInfo Term,
        IReadOnlyList<ClassroomWeekCourse> Courses);

    private sealed record LessonEntry(ClassroomLesson Lesson, long? StartAt, LessonAccess Access);

    private sealed class CourseBuilder
    {
        private readonly List<LessonEntry> _entries = [];
        private readonly HashSet<string> _lessonIds = new(StringComparer.Ordinal);

        public CourseBuilder(ClassroomCourse course) => Course = course;

        public ClassroomCourse Course { get; }

        public IReadOnlyList<LessonEntry> Entries => _entries;

        public bool TryReserve(string lessonId) => _lessonIds.Add(lessonId);

        public void Add(LessonEntry entry) => _entries.Add(entry);

        public IReadOnlyList<ClassroomLesson> BuildLessons() => _entries
            .OrderBy(entry => entry.Lesson.DateText ?? "\uFFFF", StringComparer.Ordinal)
            .ThenBy(entry => entry.StartAt ?? long.MaxValue)
            .Select(entry => entry.Lesson)
            .ToArray();
    }

    private sealed class ClassroomCatalog
    {
        public static ClassroomCatalog Empty { get; } = new()
        {
            Courses = Array.Empty<ClassroomCourse>(),
            LessonsByCourse = new Dictionary<string, IReadOnlyList<ClassroomLesson>>(
                StringComparer.Ordinal),
            Access = NoLessons
        };

        public required IReadOnlyList<ClassroomCourse> Courses { get; init; }

        public required IReadOnlyDictionary<string, IReadOnlyList<ClassroomLesson>> LessonsByCourse
        {
            get;
            init;
        }

        public required IReadOnlyDictionary<string, LessonAccess> Access { get; init; }
    }

    private sealed class ClassroomSourceFailureException : Exception
    {
        public ClassroomSourceFailureException(
            ClassroomFailureReason reason,
            string message,
            bool sessionExpired)
            : base(message)
        {
            Reason = reason;
            SessionExpired = sessionExpired;
        }

        public ClassroomFailureReason Reason { get; }

        public bool SessionExpired { get; }
    }
}
