using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace VsCleaner;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<CleanItem> _items = new();
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        TxtRoot.Text = GetDefaultRoot();
        GridResults.ItemsSource = _items;
    }

    private static string GetDefaultRoot()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var vs = Path.Combine(docs, "Visual Studio 2022", "Projects");
        if (Directory.Exists(vs)) return vs;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string Root => TxtRoot.Text.Trim().Trim('"');

    private bool ValidRoot()
    {
        if (!Directory.Exists(Root))
        {
            MessageBox.Show("Folder tidak ditemukan:\n" + Root, "VS Cleaner",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void SetBusy(bool busy)
    {
        BtnScan.IsEnabled = !busy;
        BtnOrphan.IsEnabled = !busy;
        BtnGitignore.IsEnabled = !busy;
        BtnClean.IsEnabled = !busy;
        BtnCancel.IsEnabled = busy;
        Progress.IsIndeterminate = busy;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pilih folder root proyek / solution" };
        if (dlg.ShowDialog() == true)
            TxtRoot.Text = dlg.FolderName;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void BtnAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsSelected = true;
        UpdateTotal();
    }

    private void BtnNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsSelected = false;
        UpdateTotal();
    }

    private CleanItem? SelectedItem() => GridResults.SelectedItem as CleanItem;

    private void BtnOpen_Click(object sender, RoutedEventArgs e) => OpenSelectedInExplorer();

    private void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenSelectedInExplorer();

    private void MenuSelect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem() != null) SelectedItem()!.IsSelected = true;
        UpdateTotal();
    }

    private void MenuDeselect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem() != null) SelectedItem()!.IsSelected = false;
        UpdateTotal();
    }

    private void GridResults_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedInExplorer();

    private void OpenSelectedInExplorer()
    {
        var item = SelectedItem();
        if (item == null)
        {
            MessageBox.Show("Pilih dulu satu baris di tabel.", "VS Cleaner",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            ExplorerUtil.Open(item.FullPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Gagal membuka Explorer:\n" + ex.Message, "VS Cleaner",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidRoot()) return;

        var opts = new ScanOptions(
            IncludeBin: ChkBin.IsChecked == true,
            IncludeObj: ChkObj.IsChecked == true,
            IncludeVs: ChkVs.IsChecked == true,
            IncludeTest: ChkTest.IsChecked == true,
            IncludeCppOutput: ChkCpp.IsChecked == true,
            IncludeUserFiles: ChkUser.IsChecked == true);

        if (!opts.Any)
        {
            MessageBox.Show("Centang minimal satu target pembersihan.", "VS Cleaner",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        _items.Clear();
        UpdateTotal();
        var root = Root; // tangkap di UI thread, jangan baca TxtRoot dari Task.Run

        try
        {
            var progress = new Progress<string>(s => TxtStatus.Text = s);
            var found = await Task.Run(() => Scanner.Scan(root, opts, progress, _cts.Token));

            _items.Clear();
            foreach (var f in found.OrderByDescending(x => x.SizeBytes))
            {
                f.PropertyChanged += (_, __) => UpdateTotal();
                _items.Add(f);
            }
            UpdateTotal();
            TxtStatus.Text = $"Selesai. {_items.Count} item basi ditemukan.";
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Dibatalkan."; }
        catch (Exception ex)
        {
            MessageBox.Show("Gagal scan:\n" + ex.Message, "VS Cleaner",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); _cts.Dispose(); _cts = null; }
    }

    private async void BtnOrphan_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidRoot()) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var root = Root;
        try
        {
            var progress = new Progress<string>(s => TxtStatus.Text = s);
            var found = await Task.Run(() => OrphanScanner.Scan(root, progress, _cts.Token));

            var existing = new HashSet<string>(_items.Select(x => x.FullPath),
                StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var f in found.OrderByDescending(x => x.SizeBytes))
            {
                if (existing.Contains(f.FullPath)) continue;
                f.IsSelected = false; // paksa manual review
                f.PropertyChanged += (_, __) => UpdateTotal();
                _items.Add(f);
                added++;
            }
            UpdateTotal();
            TxtStatus.Text = $"Scan orphan selesai. {added} kandidat baru (tidak dicentang, periksa manual).";
            if (added > 0)
                MessageBox.Show(
                    $"{added} kandidat file tak relevan ditemukan.\n\n" +
                    "Jenis 'sampah' (tmp/log/bak) relatif aman.\n" +
                    "Jenis 'orphan?' dan 'tak terpakai?' WAJIB diperiksa manual: " +
                    "klik kanan → Buka di Explorer sebelum hapus.",
                    "VS Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("Tidak ada kandidat orphan baru.", "VS Cleaner",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Dibatalkan."; }
        catch (Exception ex)
        {
            MessageBox.Show("Gagal scan orphan:\n" + ex.Message, "VS Cleaner",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); _cts.Dispose(); _cts = null; }
    }

    private async void BtnGitignore_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidRoot()) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var root = Root;
        try
        {
            var progress = new Progress<string>(s => TxtStatus.Text = s);
            var (found, hasRules) = await Task.Run(() => GitignoreScanner.Scan(root, progress, _cts.Token));

            if (!hasRules)
            {
                MessageBox.Show("Tidak ada file .gitignore di folder ini.", "VS Cleaner",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var existing = new HashSet<string>(_items.Select(x => x.FullPath),
                StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var f in found.OrderByDescending(x => x.SizeBytes))
            {
                if (existing.Contains(f.FullPath)) continue;
                f.IsSelected = false;
                f.PropertyChanged += (_, __) => UpdateTotal();
                _items.Add(f);
                added++;
            }
            UpdateTotal();
            TxtStatus.Text = $"Scan .gitignore selesai. {added} kandidat baru (tidak dicentang, periksa manual).";
        }
        catch (OperationCanceledException) { TxtStatus.Text = "Dibatalkan."; }
        catch (Exception ex)
        {
            MessageBox.Show("Gagal scan .gitignore:\n" + ex.Message, "VS Cleaner",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); _cts.Dispose(); _cts = null; }
    }

    private async void BtnClean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Tidak ada item yang dipilih.", "VS Cleaner",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool recycle = ChkRecycle.IsChecked == true;
        long total = selected.Sum(x => x.SizeBytes);
        var confirm = MessageBox.Show(
            $"{(recycle ? "Buang ke Recycle Bin" : "Hapus PERMANEN")} {selected.Count} item ({FormatSize(total)})?\n\n" +
            (recycle ? "Bisa di-restore dari Recycle Bin." : "TIDAK bisa di-restore.") +
            "\nPastikan Visual Studio sudah ditutup.",
            "Konfirmasi hapus", MessageBoxButton.YesNo,
            recycle ? MessageBoxImage.Question : MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        int ok = 0, fail = 0;

        await Task.Run(() =>
        {
            foreach (var item in selected)
            {
                try
                {
                    if (recycle) Cleaner.DeleteToRecycle(item.FullPath);
                    else Cleaner.DeletePermanent(item.FullPath);
                    ok++;
                }
                catch { fail++; }
                Dispatcher.Invoke(() =>
                    TxtStatus.Text = $"Menghapus {ok + fail}/{selected.Count}: {item.Name}");
            }
        });

        foreach (var s in selected)
        {
            if (!Directory.Exists(s.FullPath) && !File.Exists(s.FullPath))
                _items.Remove(s);
        }
        UpdateTotal();
        SetBusy(false);
        TxtStatus.Text = $"Selesai. Berhasil: {ok}, gagal: {fail}.";
        if (fail > 0)
            MessageBox.Show($"{fail} item gagal (mungkin terkunci atau path terlalu panjang). Tutup VS lalu coba lagi.",
                "VS Cleaner", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UpdateTotal()
    {
        var sel = _items.Where(x => x.IsSelected).ToList();
        TxtTotal.Text = $"{_items.Count} item • dipilih {sel.Count} • {FormatSize(sel.Sum(x => x.SizeBytes))} akan dibebaskan";
    }

    public static string FormatSize(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}

public sealed record ScanOptions(
    bool IncludeBin, bool IncludeObj, bool IncludeVs,
    bool IncludeTest, bool IncludeCppOutput, bool IncludeUserFiles)
{
    public bool Any => IncludeBin || IncludeObj || IncludeVs || IncludeTest || IncludeCppOutput || IncludeUserFiles;
}

public sealed class CleanItem : INotifyPropertyChanged
{
    private bool _selected = true;
    public bool IsSelected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Detail { get; set; } = "";
    public long SizeBytes { get; set; }
    public string SizeDisplay => MainWindow.FormatSize(SizeBytes);
    public DateTime Modified { get; set; }
    public string ModifiedDisplay => Modified == default ? "-" : Modified.ToString("yyyy-MM-dd HH:mm");
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class ExplorerUtil
{
    public static void Open(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir))
                Process.Start("explorer.exe", $"\"{dir}\"");
            else throw new FileNotFoundException("Path tidak ditemukan: " + path);
        }
    }
}

public static class Scanner
{
    public static List<CleanItem> Scan(string root, ScanOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new List<CleanItem>();
        var stack = new Stack<string>();
        stack.Push(root);
        int visited = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { continue; }

            visited++;
            if (visited % 50 == 0) progress?.Report($"Memindai: {dir}");

            if (o.IncludeUserFiles)
            {
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    bool hit = ext is ".user" or ".suo" or ".userosscache" or ".sdf" or ".opensdf" or ".ncb"
                        || name == "project.lock.json"
                        || ext is ".vc.db" or ".opendb" || name.EndsWith(".vc.db");
                    if (hit)
                    {
                        var fi = SafeFileInfo(f);
                        if (fi != null) result.Add(new CleanItem
                        {
                            Kind = "cache file", Name = Path.GetFileName(f),
                            FullPath = f, SizeBytes = fi.Length, Modified = fi.LastWriteTime,
                            Detail = "file cache VS"
                        });
                    }
                }
            }

            foreach (var sub in subdirs)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(sub);

                if (IsTargetDir(name, o, sub, out var kind))
                {
                    var (size, modified) = DirSize(sub);
                    result.Add(new CleanItem
                    {
                        Kind = kind, Name = name,
                        FullPath = sub, SizeBytes = size, Modified = modified,
                        Detail = "folder build"
                    });
                    progress?.Report($"Ditemukan: {sub}");
                    continue;
                }
                stack.Push(sub);
            }
        }
        return result;
    }

    private static bool IsTargetDir(string name, ScanOptions o, string fullPath, out string kind)
    {
        kind = "";
        if (o.IncludeBin && Eq(name, "bin")) { kind = "bin"; return true; }
        if (o.IncludeObj && Eq(name, "obj")) { kind = "obj"; return true; }
        if (o.IncludeVs && Eq(name, ".vs")) { kind = ".vs"; return true; }
        if (o.IncludeTest && (Eq(name, "testresults") || Eq(name, "testresult"))) { kind = "test"; return true; }
        if (Eq(name, ".sonarqube") || Eq(name, "benchmarkdotnet.artifacts")) { kind = "analisis"; return true; }

        if (o.IncludeCppOutput && (Eq(name, "debug") || Eq(name, "release") || Eq(name, "x64") || Eq(name, "x86") || Eq(name, "arm64")))
        {
            try
            {
                var parent = Path.GetDirectoryName(fullPath);
                if (parent != null && Directory.GetFiles(parent, "*.sln").Length + Directory.GetFiles(parent, "*.vcxproj").Length
                    + Directory.GetFiles(parent, "*.csproj").Length + Directory.GetFiles(parent, "*.vcxitems").Length > 0)
                {
                    kind = "c++ output"; return true;
                }
            }
            catch { }
            return false;
        }
        return false;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static FileInfo? SafeFileInfo(string p)
    {
        try { return new FileInfo(p); } catch { return null; }
    }

    internal static (long size, DateTime modified) DirSize(string dir)
    {
        long size = 0; DateTime mod = default;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(f);
                    size += fi.Length;
                    if (fi.LastWriteTime > mod) mod = fi.LastWriteTime;
                }
                catch { }
            }
        }
        catch { }
        if (mod == default)
        {
            try { mod = Directory.GetLastWriteTime(dir); } catch { }
        }
        return (size, mod);
    }
}

/// <summary>Heuristik file tak terpakai; hasil wajib direview manual.</summary>
public static class OrphanScanner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", ".svn", "testresults", "testresult",
        "packages", "node_modules", ".sonarqube", "benchmarkdotnet.artifacts", "ipch", ".idea",
        "publish", "publish-fd"
    };

    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", "desktop.ini", ".ds_store"
    };

    private static readonly HashSet<string> JunkExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".log", ".bak", ".old", ".orig", ".rej", ".swp",
        ".ilk", ".ipdb", ".iobj", ".tlog", ".lastbuildstate", ".unsuccessfulbuild", ".pch", ".sbr"
    };

    public static List<CleanItem> Scan(string root, IProgress<string>? progress, CancellationToken ct)
    {
        var projFiles = EnumerateFilesSafe(root, ct)
            .Where(f => IsProjDef(f)).ToList();

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var classicCppIncludes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        bool hasClassicCSharp = false;

        foreach (var pf in projFiles)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(pf);
            if (dir != null) projectDirs.Add(dir);
            referenced.Add(Path.GetFileName(pf));

            string text;
            try
            {
                var fi = new FileInfo(pf);
                if (fi.Length > 4_000_000) continue;
                text = File.ReadAllText(pf);
            }
            catch { continue; }

            // Semua token nama file dalam teks proyek dianggap referensi
            foreach (var token in TokenizeFileNames(text))
                referenced.Add(token);

            if (pf.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase) ||
                pf.EndsWith(".vcxitems", StringComparison.OrdinalIgnoreCase) ||
                pf.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                pf.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var doc = XDocument.Parse(text);
                    var includes = doc.Descendants()
                        .Attributes("Include")
                        .Select(a => a.Value)
                        .Select(v => Path.GetFileName(v.Split(';')[0].Trim()))
                        .Where(n => n.Length > 0);
                    foreach (var inc in includes) referenced.Add(inc);

                    bool isSdk = doc.Root?.Attribute("Sdk") != null;
                    if (!isSdk && dir != null)
                    {
                        if (!classicCppIncludes.TryGetValue(dir, out var set))
                            classicCppIncludes[dir] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var inc in includes) set.Add(inc);
                        if (pf.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) hasClassicCSharp = true;
                    }
                }
                catch { /* bukan XML valid (misal SDK props) -> abaikan */ }
            }
        }

        var result = new List<CleanItem>();
        int n = 0;
        foreach (var f in EnumerateFilesSafe(root, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (++n % 500 == 0) progress?.Report($"Cek keterhubungan: {n} file… ({result.Count} kandidat)");

            if (IsUnderSkipDir(root, f)) continue;
            var name = Path.GetFileName(f);
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (IsProjDef(f)) continue;

            FileInfo fi;
            try { fi = new FileInfo(f); }
            catch { continue; }

            if (JunkNames.Contains(name) || JunkExts.Contains(ext) || name.EndsWith("~"))
            {
                result.Add(new CleanItem
                {
                    Kind = "sampah", Name = name, FullPath = f,
                    SizeBytes = SafeLen(fi), Modified = SafeTime(fi),
                    Detail = $"ekstensi/nama sementara ({ext})"
                });
                continue;
            }

            if (ext is ".dll" or ".exe" or ".lib" or ".zip" or ".7z" or ".rar" or ".msi" or ".iso" or ".vhd" or ".vhdx" or ".pdb")
            {
                if (!referenced.Contains(name) && fi.Length > 2_000_000)
                {
                    result.Add(new CleanItem
                    {
                        Kind = "tak terpakai?", Name = name, FullPath = f,
                        SizeBytes = fi.Length, Modified = SafeTime(fi),
                        Detail = "biner besar, nama tak disebut di .sln/.csproj"
                    });
                }
                continue;
            }

            // Hanya proyek klasik yang mencatat file eksplisit.
            if (ext is ".cpp" or ".cxx" or ".cc" or ".c" or ".h" or ".hpp" or ".xaml" or ".resx")
            {
                var enclosing = EnclosingProjectDir(f, classicCppIncludes.Keys);
                if (enclosing != null && !classicCppIncludes[enclosing].Contains(name))
                {
                    result.Add(new CleanItem
                    {
                        Kind = "orphan?", Name = name, FullPath = f,
                        SizeBytes = SafeLen(fi), Modified = SafeTime(fi),
                        Detail = $"tak terdaftar di {Path.GetFileName(enclosing)} (.vcxproj)"
                    });
                    continue;
                }
                if (hasClassicCSharp && ext is ".cs" or ".xaml" or ".resx")
                {
                    var enc = EnclosingProjectDir(f, classicCppIncludes.Keys);
                    if (enc != null && !classicCppIncludes[enc].Contains(name) && !referenced.Contains(name))
                    {
                        result.Add(new CleanItem
                        {
                            Kind = "orphan?", Name = name, FullPath = f,
                            SizeBytes = SafeLen(fi), Modified = SafeTime(fi),
                            Detail = "tak terdaftar di proyek klasik"
                        });
                        continue;
                    }
                }
            }

            if (ext is ".cs" or ".cpp" or ".h" or ".vb")
            {
                if (!IsUnderAnyDir(f, projectDirs) && !referenced.Contains(name))
                {
                    result.Add(new CleanItem
                    {
                        Kind = "orphan?", Name = name, FullPath = f,
                        SizeBytes = SafeLen(fi), Modified = SafeTime(fi),
                        Detail = "di luar folder proyek + tak dirujuk"
                    });
                }
            }
        }
        return result;
    }

    private static bool IsProjDef(string f)
    {
        var ext = Path.GetExtension(f).ToLowerInvariant();
        return ext is ".sln" or ".slnx" or ".csproj" or ".vbproj" or ".vcxproj"
            or ".vcxitems" or ".props" or ".targets" or ".slnf";
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] subs, files;
            try { subs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in subs)
            {
                if (SkipDirs.Contains(Path.GetFileName(s))) continue;
                stack.Push(s);
            }
        }
    }

    private static bool IsUnderSkipDir(string root, string file)
    {
        var dir = Path.GetDirectoryName(file);
        while (dir != null && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (SkipDirs.Contains(Path.GetFileName(dir))) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    private static bool IsUnderAnyDir(string file, HashSet<string> dirs)
    {
        foreach (var d in dirs)
        {
            if (file.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? EnclosingProjectDir(string file, IEnumerable<string> projDirs)
    {
        string? best = null;
        foreach (var d in projDirs)
        {
            if (file.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                if (best == null || d.Length > best.Length) best = d;
        }
        return best;
    }

    private static IEnumerable<string> TokenizeFileNames(string text)
    {
        int len = text.Length;
        int start = -1;
        for (int i = 0; i <= len; i++)
        {
            char c = i < len ? text[i] : ' ';
            bool ok = char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '+';
            if (ok) { if (start < 0) start = i; }
            else
            {
                if (start >= 0)
                {
                    int l = i - start;
                    if (l >= 3 && l <= 120)
                    {
                        var tok = text.Substring(start, l);
                        int dot = tok.LastIndexOf('.');
                        if (dot > 0 && tok.Length - dot - 1 is >= 1 and <= 6)
                            yield return tok;
                    }
                    start = -1;
                }
            }
        }
    }

    private static long SafeLen(FileInfo fi) { try { return fi.Length; } catch { return 0; } }
    private static DateTime SafeTime(FileInfo fi) { try { return fi.LastWriteTime; } catch { return default; } }
}

/// <summary>Cocokkan semua file/folder terhadap pola .gitignore; hasil wajib direview manual.</summary>
public static class GitignoreScanner
{
    private sealed class Rule
    {
        public Regex Regex = null!;
        public bool DirOnly;
        public bool Negated;
        public string Original = "";
    }

    public static (List<CleanItem> items, bool hasRules) Scan(string root, IProgress<string>? progress, CancellationToken ct)
    {
        var sets = new List<(string dir, List<Rule> rules)>();
        foreach (var g in EnumerateGitignores(root, ct))
        {
            var rules = Parse(g);
            if (rules.Count > 0)
                sets.Add((Path.GetDirectoryName(g)!, rules));
        }

        var result = new List<CleanItem>();
        if (sets.Count == 0) return (result, false);

        var stack = new Stack<string>();
        stack.Push(root);
        int n = 0;
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] subs, files;
            try { subs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var sub in subs)
            {
                ct.ThrowIfCancellationRequested();
                if (IsGitDir(root, sub)) continue;
                if (Match(sets, sub, isDir: true, out var pat))
                {
                    var (size, mod) = Scanner.DirSize(sub);
                    result.Add(new CleanItem
                    {
                        Kind = "gitignore", Name = Path.GetFileName(sub), FullPath = sub,
                        SizeBytes = size, Modified = mod, Detail = $"pola '{pat}'"
                    });
                    progress?.Report($"Ditemukan: {sub}");
                    continue;
                }
                stack.Push(sub);
            }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(f) == ".gitignore") continue;
                if (++n % 500 == 0) progress?.Report($"Cek .gitignore: {n} file… ({result.Count} kandidat)");
                if (!Match(sets, f, isDir: false, out var pat)) continue;
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                result.Add(new CleanItem
                {
                    Kind = "gitignore", Name = Path.GetFileName(f), FullPath = f,
                    SizeBytes = fi.Length, Modified = fi.LastWriteTime, Detail = $"pola '{pat}'"
                });
            }
        }
        return (result, true);
    }

    private static bool Match(List<(string dir, List<Rule> rules)> sets, string path, bool isDir, out string pattern)
    {
        pattern = "";
        bool ignored = false;
        foreach (var (dir, rules) in sets)
        {
            string rel = ToSlash(Path.GetRelativePath(dir, path));
            if (rel == "." || rel.StartsWith("..")) continue;
            foreach (var r in rules)
            {
                if (r.DirOnly && !isDir) continue;
                if (r.Regex.IsMatch(rel)) { ignored = !r.Negated; pattern = r.Original; }
            }
        }
        return ignored;
    }

    private static List<Rule> Parse(string gitignorePath)
    {
        var rules = new List<Rule>();
        string[] lines;
        try { lines = File.ReadAllLines(gitignorePath); } catch { return rules; }
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            bool negated = false;
            if (line.StartsWith("!")) { negated = true; line = line[1..]; }
            else if (line.StartsWith("\\#") || line.StartsWith("\\!")) line = line[1..];
            if (line.Length == 0) continue;
            bool dirOnly = line.EndsWith("/");
            line = line.TrimEnd('/');
            bool rooted = line.StartsWith("/") || line.Contains("/");
            if (rooted) line = line.TrimStart('/');
            string core = GlobToRegex(line);
            string rx = rooted ? $"^{core}(/.*)?$" : $"^(.*/)?{core}(/.*)?$";
            try
            {
                rules.Add(new Rule
                {
                    Regex = new Regex(rx, RegexOptions.IgnoreCase),
                    DirOnly = dirOnly, Negated = negated, Original = (negated ? "!" : "") + raw.Trim()
                });
            }
            catch { }
        }
        return rules;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    if (i + 2 < glob.Length && glob[i + 2] == '/') { sb.Append("(.*/)?"); i += 2; }
                    else { sb.Append(".*"); i++; }
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else if (c == '[')
            {
                int end = glob.IndexOf(']', i + 1);
                if (end < 0) sb.Append(Regex.Escape("["));
                else
                {
                    var cls = glob.Substring(i + 1, end - i - 1);
                    if (cls.StartsWith("!")) cls = "^" + cls[1..];
                    sb.Append('[').Append(cls).Append(']');
                    i = end;
                }
            }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }

    private static IEnumerable<string> EnumerateGitignores(string root, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] subs, files;
            try { subs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir, ".gitignore"); }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in subs)
            {
                if (IsGitDir(root, s)) continue;
                stack.Push(s);
            }
        }
    }

    private static bool IsGitDir(string root, string path)
    {
        var git = Path.Combine(root, ".git");
        return path.Equals(git, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(git + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToSlash(string p) => p.Replace(Path.DirectorySeparatorChar, '/');
}

public static class Cleaner
{
    public static void DeletePermanent(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, recursive: true);
    }

    public static void DeleteToRecycle(string path)
    {
        if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return;
        }
        if (Directory.Exists(path))
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return;
        }
    }

    [Obsolete("Gunakan DeletePermanent / DeleteToRecycle")]
    public static void Delete(string path) => DeletePermanent(path);
}
