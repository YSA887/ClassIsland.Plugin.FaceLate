using System.Collections.ObjectModel;
using System.Text;
using ClassIsland.Plugin.FaceLate.Models;
using ClassIsland.Shared.Helpers;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Plugin.FaceLate.Services;

/// <summary>
/// 花名册与人脸库。数据保存在插件配置目录的 roster.json 中，
/// 人脸特征向量随花名册一起保存，无需任何联网或云端服务。
/// </summary>
public sealed class RosterService
{
    /// <summary>
    /// 单个学生最多保留的人脸样本数。样本越多样本越好，但过多收益递减且拖慢比对。
    /// </summary>
    public const int MaxSamplesPerStudent = 12;

    private readonly ILogger<RosterService> _logger;
    private readonly object _sync = new();
    private RosterDocument _document = new();
    private string _filePath = "";
    private bool _loaded;

    public RosterService(ILogger<RosterService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 花名册数据。首次访问时会自动从磁盘载入。
    /// </summary>
    public RosterDocument Document
    {
        get
        {
            EnsureLoaded();
            return _document;
        }
        private set => _document = value;
    }

    /// <summary>
    /// 学生列表，可直接绑定到界面。
    /// </summary>
    public ObservableCollection<Student> Students
    {
        get
        {
            EnsureLoaded();
            return _document.Students;
        }
    }

    /// <summary>
    /// 花名册或人脸库发生变化时触发。
    /// </summary>
    public event EventHandler? RosterChanged;

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            Load();
        }
    }

    /// <summary>
    /// 从磁盘载入花名册。
    /// </summary>
    public void Load()
    {
        lock (_sync)
        {
            if (_loaded)
            {
                return;
            }

            _filePath = Path.Combine(FaceLatePaths.PluginConfigFolder, "roster.json");
            RosterDocument? loaded = null;
            try
            {
                loaded = ConfigureFileHelper.LoadConfig<RosterDocument>(_filePath);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "FaceLate 载入花名册失败，将使用空花名册");
            }

            _document = loaded ?? new RosterDocument();
            if (_document.Students == null!)
            {
                _document.Students = new ObservableCollection<Student>();
            }

            foreach (var student in _document.Students)
            {
                if (student.Faces == null!)
                {
                    student.Faces = new ObservableCollection<FaceSample>();
                }
            }

            _loaded = true;
        }

        RaiseChanged();
    }

    /// <summary>
    /// 保存花名册到磁盘。
    /// </summary>
    public void Save()
    {
        lock (_sync)
        {
            if (!_loaded || string.IsNullOrEmpty(_filePath))
            {
                return;
            }

            try
            {
                ConfigureFileHelper.SaveConfig(_filePath, Document);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "FaceLate 保存花名册失败");
            }
        }
    }

    /// <summary>
    /// 通知界面数据已变化。
    /// </summary>
    /// <param name="save">
    /// 是否顺便落盘。批量导入几百张照片时，每加一条就整份序列化一次会非常慢，
    /// 这种场合传 <c>false</c>，等整批做完再统一保存一次。
    /// </param>
    public void RaiseChanged(bool save = true)
    {
        if (save)
        {
            Save();
        }

        RosterChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按姓名查一位同学；<paramref name="className"/> 为空时只看姓名。
    /// </summary>
    public Student? FindByName(string name, string? className = null)
    {
        var target = (name ?? "").Trim();
        if (target.Length == 0)
        {
            return null;
        }

        lock (_sync)
        {
            return Students.FirstOrDefault(x =>
                string.Equals(x.Name, target, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(className) || string.Equals(x.ClassName, className, StringComparison.Ordinal)));
        }
    }

    /// <summary>
    /// 参与考勤的学生（已启用且未请假）。
    /// </summary>
    public List<Student> GetActiveStudents()
    {
        lock (_sync)
        {
            return Students.Where(x => x.Enabled && !x.OnLeave).ToList();
        }
    }

    /// <summary>
    /// 新增一位学生。
    /// </summary>
    public Student AddStudent(string name, string className = "")
    {
        var student = new Student { Name = name.Trim(), ClassName = className.Trim() };
        // 学生列表是直接绑到界面上的，增删必须在 UI 线程完成。
        UiThread.RunSync(() =>
        {
            lock (_sync)
            {
                Students.Add(student);
            }
        });

        RaiseChanged();
        return student;
    }

    /// <summary>
    /// 删除一位学生。
    /// </summary>
    public bool RemoveStudent(Student student)
    {
        var removed = false;
        UiThread.RunSync(() =>
        {
            lock (_sync)
            {
                removed = Students.Remove(student);
            }
        });

        if (removed)
        {
            TryDeleteFaceFolder(student);
            RaiseChanged();
        }

        return removed;
    }

    /// <summary>
    /// 为一位学生追加人脸样本。
    /// </summary>
    public bool AddFaceSample(Student student, float[] vector, string source, string? snapshotFile, bool save = true)
    {
        if (vector.Length == 0)
        {
            return false;
        }

        // student.Faces 也是绑在界面上的集合，改它同样要切回 UI 线程。
        UiThread.RunSync(() =>
        {
            lock (_sync)
            {
                if (student.Faces.Count >= MaxSamplesPerStudent)
                {
                    // 超出上限时替换掉最早的一条，保证样本不过期也不无限增长。
                    student.Faces.RemoveAt(0);
                }

                student.Faces.Add(new FaceSample
                {
                    Vector = vector,
                    Source = source,
                    CreatedAt = DateTime.Now,
                    SnapshotFile = snapshotFile ?? "",
                });
            }
        });

        student.NotifyFacesChanged();
        RaiseChanged(save);
        return true;
    }

    /// <summary>
    /// 清空某位学生的全部人脸样本。
    /// </summary>
    public void ClearFaces(Student student)
    {
        UiThread.RunSync(() =>
        {
            lock (_sync)
            {
                student.Faces.Clear();
            }
        });

        TryDeleteFaceFolder(student);
        student.NotifyFacesChanged();
        RaiseChanged();
    }

    /// <summary>
    /// 构建用于比对的人脸库。
    /// <para>
    /// 这里**不要**按固定维度过滤（旧版写死了 <c>Vector.Length == 128</c>）：
    /// 换成 ArcFace（512 维）之后所有人脸都会被过滤掉，人脸库变成空的，
    /// 界面却报「还没有录入人脸样本」，非常难查。
    /// 维度是否匹配交给 <c>FaceEngine.Similarity</c> 判断（维度不同直接返回 0）。
    /// </para>
    /// </summary>
    public List<GalleryEntry> BuildGallery()
    {
        var gallery = new List<GalleryEntry>();
        lock (_sync)
        {
            foreach (var student in Students)
            {
                if (!student.Enabled || student.OnLeave)
                {
                    continue;
                }

                foreach (var sample in student.Faces)
                {
                    if (sample.Vector.Length > 0)
                    {
                        gallery.Add(new GalleryEntry { Student = student, Feature = sample.Vector });
                    }
                }
            }
        }

        return gallery;
    }

    /// <summary>
    /// 按文件名猜这张照片属于花名册里的谁。
    /// <para>
    /// 「ABCD 四人，批量导入 806班A.jpg、806班B.jpg」这种场景就是靠这里：
    /// 把扩展名去掉后去花名册里找名字，命中就直接自动导入，不用一张张手点。
    /// </para>
    /// </summary>
    /// <param name="fileName">文件名（可以带路径）。</param>
    public NameMatchResult MatchByFileName(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName ?? "");
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return NameMatchResult.None("文件名为空");
        }

        var normalized = NormalizeForMatch(baseName);
        if (normalized.Length == 0)
        {
            return NameMatchResult.None("文件名里没有可用字符");
        }

        Student? best = null;
        var bestScore = 0;
        var tie = false;

        lock (_sync)
        {
            foreach (var student in Students)
            {
                var name = NormalizeForMatch(student.Name);
                if (name.Length == 0)
                {
                    continue;
                }

                var score = ScoreName(normalized, name);
                if (score <= 0)
                {
                    continue;
                }

                // 文件名里同时出现了该同学的班级，多半就是他，加分。
                if (!string.IsNullOrWhiteSpace(student.ClassName)
                    && baseName.Contains(student.ClassName, StringComparison.Ordinal))
                {
                    score += 40;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = student;
                    tie = false;
                }
                else if (score == bestScore && best != null && !ReferenceEquals(best, student))
                {
                    tie = true;
                }
            }
        }

        if (best == null)
        {
            return NameMatchResult.None("文件名里没有匹配到花名册中的同学");
        }

        return tie
            ? NameMatchResult.Ambiguous("文件名同时能对上多位同学，无法确定是谁")
            : new NameMatchResult { Student = best, Score = bestScore };
    }

    /// <summary>去掉空白与常见分隔符，拉丁字母统一小写，便于比较。</summary>
    private static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (ch is '-' or '_' or '.' or '·' or '(' or ')' or '[' or ']'
                or '（' or '）' or '【' or '】' or '、' or '/' or '\\')
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 给「文件名里出现姓名」打分。分值本身就是优先级：
    /// 完全相等 &gt; 开头/结尾 &gt; 中途出现。
    /// </summary>
    private static int ScoreName(string normalizedBase, string normalizedName)
    {
        if (normalizedBase == normalizedName)
        {
            return 1000;
        }

        var starts = normalizedBase.StartsWith(normalizedName, StringComparison.Ordinal);
        var ends = normalizedBase.EndsWith(normalizedName, StringComparison.Ordinal);

        // 短的姓名（如单个字母 A、两个字的中文名）很容易在无关文本里撞上，
        // 所以只有出现在开头或结尾才给高分，中途出现只给很低的分，
        // 这样「806班A-1.jpg」能对上 A，而「IMG20260912」不会把所有人对上。
        if (normalizedName.Length <= 2)
        {
            if (starts)
            {
                return 600 + normalizedName.Length;
            }

            if (ends)
            {
                return 580 + normalizedName.Length;
            }

            return normalizedBase.Contains(normalizedName, StringComparison.Ordinal) ? 50 + normalizedName.Length : 0;
        }

        if (starts)
        {
            return 500 + normalizedName.Length;
        }

        if (ends)
        {
            return 490 + normalizedName.Length;
        }

        return normalizedBase.Contains(normalizedName, StringComparison.Ordinal) ? 100 + normalizedName.Length : 0;
    }

    /// <summary>
    /// 从文本批量导入花名册。每行一位同学，支持「姓名」或「姓名,班级」或「姓名 班级」。
    /// </summary>
    /// <param name="text">文本内容。</param>
    /// <param name="defaultClassName">未指定班级时使用的班级名。</param>
    /// <returns>实际新增的人数。</returns>
    public int ImportFromText(string text, string defaultClassName)
    {
        var added = 0;
        var separators = new[] { ',', '，', '\t', ' ' };
        lock (_sync)
        {
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var parts = line.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                var name = parts[0];
                var className = parts.Length > 1 ? parts[1] : defaultClassName;
                if (Students.Any(x => x.Name == name && x.ClassName == className))
                {
                    continue;
                }

                var student = new Student { Name = name, ClassName = className };
                UiThread.RunSync(() =>
                {
                    lock (_sync)
                    {
                        Students.Add(student);
                    }
                });
                added++;
            }
        }

        if (added > 0)
        {
            RaiseChanged();
        }

        return added;
    }

    /// <summary>
    /// 导出花名册为 CSV 文本。
    /// </summary>
    public string ExportToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("姓名,班级,请假,参与考勤,人脸样本数,备注");
        lock (_sync)
        {
            foreach (var student in Students)
            {
                sb.AppendLine(string.Join(',',
                    Escape(student.Name),
                    Escape(student.ClassName),
                    student.OnLeave ? "是" : "否",
                    student.Enabled ? "是" : "否",
                    student.Faces.Count.ToString(),
                    Escape(student.Note)));
            }
        }

        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private void TryDeleteFaceFolder(Student student)
    {
        try
        {
            var dir = Path.Combine(FaceLatePaths.FacesFolder, student.Id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "FaceLate 删除人脸样本图片失败");
        }
    }
}

/// <summary>
/// 「文件名 → 花名册同学」的匹配结果。
/// </summary>
public sealed class NameMatchResult
{
    /// <summary>命中的同学；没命中时为 null。</summary>
    public Student? Student { get; init; }

    /// <summary>匹配得分，仅用于排序和排查。</summary>
    public int Score { get; init; }

    /// <summary>是不是「多个同学的名字都能对上」，这种情况不敢自动导入，要交给用户确认。</summary>
    public bool IsAmbiguous { get; init; }

    /// <summary>没命中 / 有歧义时的说明。</summary>
    public string Note { get; init; } = "";

    /// <summary>构造一个「没匹配上」的结果。</summary>
    public static NameMatchResult None(string note) => new() { Note = note };

    /// <summary>构造一个「有歧义」的结果。</summary>
    public static NameMatchResult Ambiguous(string note) => new() { IsAmbiguous = true, Note = note };
}
