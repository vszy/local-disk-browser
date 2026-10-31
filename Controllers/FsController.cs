using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.VisualBasic.FileIO;

namespace LocalDiskBrowser.Controllers
{
    public record PathsRequest(string[] Paths, string? Dest, bool Permanent);
    public record RenameRequest(string Path, string NewName);
    public record NameRequest(string Path, string Name);
    public record ExtractRequest(string Path, string? Dest, bool Overwrite = false);
    public record AttrRequest(string Path, bool? ReadOnly, bool? Hidden);

    [ApiController]
    [Route("api/fs")]
    public class FsController : ControllerBase
    {
        private readonly IConfiguration _config;
        public FsController(IConfiguration config) => _config = config;

        // ---------- listing ----------

        [HttpGet("drives")]
        public IActionResult Drives()
        {
            var list = DriveInfo.GetDrives().Select(d =>
            {
                try
                {
                    if (!d.IsReady)
                        return new { name = d.Name, label = "", type = d.DriveType.ToString(), format = "", total = 0L, free = 0L, ready = false };
                    return new { name = d.Name, label = d.VolumeLabel, type = d.DriveType.ToString(), format = d.DriveFormat, total = d.TotalSize, free = d.AvailableFreeSpace, ready = true };
                }
                catch
                {
                    return new { name = d.Name, label = "", type = d.DriveType.ToString(), format = "", total = 0L, free = 0L, ready = false };
                }
            });
            return Ok(list);
        }

        [HttpGet("list")]
        public IActionResult List(string path, bool hidden = true)
        {
            try
            {
                var dir = new DirectoryInfo(path);
                if (!dir.Exists) return BadRequest(new { error = "Folder not found: " + path });

                var items = new List<object>();
                foreach (var e in dir.EnumerateFileSystemInfos())
                {
                    try
                    {
                        var isDir = e.Attributes.HasFlag(FileAttributes.Directory);
                        var isHidden = e.Attributes.HasFlag(FileAttributes.Hidden) || e.Attributes.HasFlag(FileAttributes.System);
                        if (isHidden && !hidden) continue;
                        items.Add(new
                        {
                            name = e.Name,
                            path = e.FullName,
                            isDir,
                            size = isDir ? 0L : ((FileInfo)e).Length,
                            modified = e.LastWriteTime,
                            ext = isDir ? "" : e.Extension.ToLowerInvariant(),
                            attrs = e.Attributes.ToString(),
                            hidden = isHidden
                        });
                    }
                    catch { /* entry vanished or unreadable; skip */ }
                }
                var parent = dir.Parent?.FullName;
                return Ok(new { path = dir.FullName, parent, items });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpGet("search")]
        public IActionResult Search(string path, string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return BadRequest(new { error = "Empty query" });
            var results = new List<object>();
            const int max = 1000;
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            try
            {
                var pattern = q.Contains('*') || q.Contains('?') ? q : "*" + q + "*";
                foreach (var e in new DirectoryInfo(path).EnumerateFileSystemInfos(pattern, opts))
                {
                    var isDir = e.Attributes.HasFlag(FileAttributes.Directory);
                    results.Add(new
                    {
                        name = e.Name,
                        path = e.FullName,
                        isDir,
                        size = isDir ? 0L : ((FileInfo)e).Length,
                        modified = e.LastWriteTime,
                        ext = isDir ? "" : e.Extension.ToLowerInvariant(),
                        attrs = e.Attributes.ToString(),
                        hidden = e.Attributes.HasFlag(FileAttributes.Hidden)
                    });
                    if (results.Count >= max) break;
                }
                return Ok(new { path, parent = (string?)null, items = results, truncated = results.Count >= max });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---------- delete / copy / move ----------

        [HttpPost("delete")]
        public IActionResult Delete(PathsRequest req)
        {
            var errors = new List<string>();
            // The Recycle Bin goes through the Windows shell, which cannot show prompts to a service (IIS app pool) and may
            // silently do nothing. So it is only used when enabled in config AND this is an interactive (desktop) process.
            var recycle = !req.Permanent && Environment.UserInteractive && (_config.GetValue<bool?>("FileOperations:UseRecycleBin") ?? true);
            foreach (var p in req.Paths)
            {
                var how = recycle ? "Recycle Bin delete" : "permanent delete";
                try
                {
                    var isDir = Directory.Exists(p);
                    if (!isDir && !System.IO.File.Exists(p)) { errors.Add($"{p}: not found"); continue; }

                    if (recycle)
                    {
                        if (isDir) FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        else FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    else DeletePermanently(p, isDir);

                    // Never report success unless the item is really gone.
                    if (Exists(p))
                        errors.Add($"{p}: [{how}] The item still exists after the delete. Windows blocked it without an error "
                                   + $"(usually missing permission, or a prompt shown on the server's screen). Running as: {System.Security.Principal.WindowsIdentity.GetCurrent().Name}");
                }
                catch (Exception ex)
                {
                    // Include the exception type and Windows error code: they show whether it is permissions, a lock, or the Recycle Bin.
                    errors.Add($"{p}: [{how}] {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}"
                               + (ex.InnerException != null ? $" | inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "")
                               + $" | running as: {System.Security.Principal.WindowsIdentity.GetCurrent().Name}");
                }
            }
            return Result(errors);
        }

        /// <summary>Plain .NET delete (no shell). Clears the Read-only flag first, like Explorer does after confirmation.</summary>
        private static void DeletePermanently(string path, bool isDir)
        {
            if (!isDir)
            {
                System.IO.File.SetAttributes(path, System.IO.File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                System.IO.File.Delete(path);
                return;
            }
            if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, false); // junction / symlink: remove the link only, never the target's contents
                return;
            }
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                if (System.IO.File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly))
                    System.IO.File.SetAttributes(f, System.IO.File.GetAttributes(f) & ~FileAttributes.ReadOnly);
            Directory.Delete(path, true);
        }

        [HttpPost("copy")]
        public IActionResult Copy(PathsRequest req) => Transfer(req, move: false);

        [HttpPost("move")]
        public IActionResult Move(PathsRequest req) => Transfer(req, move: true);

        private IActionResult Transfer(PathsRequest req, bool move)
        {
            var errors = new List<string>();
            if (string.IsNullOrEmpty(req.Dest) || !Directory.Exists(req.Dest))
                return BadRequest(new { error = "Destination folder not found." });
            var dest = Path.GetFullPath(req.Dest);

            foreach (var src in req.Paths)
            {
                try
                {
                    var isDir = Directory.Exists(src);
                    if (!isDir && !System.IO.File.Exists(src)) { errors.Add($"{src}: not found"); continue; }

                    var name = Path.GetFileName(src.TrimEnd('\\', '/'));
                    var srcFull = Path.GetFullPath(src);

                    if (isDir && IsSameOrInside(dest, srcFull))
                    {
                        if (move || !SamePath(Path.GetDirectoryName(srcFull), dest))
                        { errors.Add($"{src}: cannot {(move ? "move" : "copy")} a folder into itself"); continue; }
                    }
                    if (move && SamePath(Path.GetDirectoryName(srcFull), dest)) continue; // no-op

                    var target = Unique(Path.Combine(dest, name), isDir);
                    if (isDir)
                    {
                        if (move)
                        {
                            // Directory.Move fails across volumes; fall back to copy+delete.
                            if (SameRoot(srcFull, target)) Directory.Move(srcFull, target);
                            else { CopyDir(srcFull, target); Directory.Delete(srcFull, true); }
                        }
                        else CopyDir(srcFull, target);
                    }
                    else
                    {
                        if (move) System.IO.File.Move(srcFull, target);
                        else System.IO.File.Copy(srcFull, target);
                    }
                }
                catch (Exception ex) { errors.Add($"{src}: {ex.Message}"); }
            }
            return Result(errors);
        }

        // ---------- rename / mkdir ----------

        [HttpPost("rename")]
        public IActionResult Rename(RenameRequest req)
        {
            try
            {
                if (!ValidName(req.NewName)) return BadRequest(new { error = "Invalid name." });
                var parent = Path.GetDirectoryName(req.Path.TrimEnd('\\', '/'))!;
                var target = Path.Combine(parent, req.NewName);
                if (Exists(target) && !string.Equals(target, req.Path, StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { error = "An item with that name already exists." });
                if (Directory.Exists(req.Path)) Directory.Move(req.Path, target);
                else System.IO.File.Move(req.Path, target);
                return Ok(new { ok = true });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("mkdir")]
        public IActionResult MkDir(NameRequest req)
        {
            try
            {
                if (!ValidName(req.Name)) return BadRequest(new { error = "Invalid name." });
                var target = Path.Combine(req.Path, req.Name);
                if (Exists(target)) return BadRequest(new { error = "An item with that name already exists." });
                Directory.CreateDirectory(target);
                return Ok(new { ok = true });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("newfile")]
        public IActionResult NewFile(NameRequest req)
        {
            try
            {
                if (!ValidName(req.Name)) return BadRequest(new { error = "Invalid name." });
                var target = Path.Combine(req.Path, req.Name);
                if (Exists(target)) return BadRequest(new { error = "An item with that name already exists." });
                using (System.IO.File.Create(target)) { }
                return Ok(new { ok = true });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---------- zip / extract ----------

        /// <summary>Zips req.Paths into a new archive inside req.Dest (a folder). Archive name is derived from the first item.</summary>
        [HttpPost("zip")]
        public IActionResult Zip(PathsRequest req)
        {
            if (req.Paths.Length == 0) return BadRequest(new { error = "Nothing selected." });
            if (string.IsNullOrEmpty(req.Dest) || !Directory.Exists(req.Dest))
                return BadRequest(new { error = "Destination folder not found." });

            string? zipPath = null;
            try
            {
                var baseName = req.Paths.Length == 1
                    ? Path.GetFileNameWithoutExtension(req.Paths[0].TrimEnd('\\', '/'))
                    : (Path.GetFileName(req.Dest.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "archive");
                zipPath = Unique(Path.Combine(req.Dest, baseName + ".zip"), false);

                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var p in req.Paths)
                    {
                        var full = Path.GetFullPath(p);
                        if (string.Equals(full, zipPath, StringComparison.OrdinalIgnoreCase)) continue;
                        var name = Path.GetFileName(full.TrimEnd('\\', '/'));
                        if (Directory.Exists(full)) AddDir(zip, full, name, zipPath);
                        else zip.CreateEntryFromFile(full, name, CompressionLevel.Optimal);
                    }
                }
                return Ok(new { ok = true, created = zipPath });
            }
            catch (Exception ex)
            {
                if (zipPath != null) try { System.IO.File.Delete(zipPath); } catch { }
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("extract")]
        public async Task<IActionResult> Extract(ExtractRequest req)
        {
            try
            {
                if (!System.IO.File.Exists(req.Path)) return BadRequest(new { error = "Archive not found." });
                var parent = Path.GetDirectoryName(Path.GetFullPath(req.Path))!;
                var lower = req.Path.ToLowerInvariant();
                var kind = lower.EndsWith(".zip") ? "zip"
                         : lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") ? "tgz"
                         : lower.EndsWith(".tar") ? "tar"
                         : lower.EndsWith(".gz") ? "gz"
                         : lower.EndsWith(".rar") ? "rar"
                         : lower.EndsWith(".7z") ? "7z" : null;
                if (kind == null) return BadRequest(new { error = "Unsupported archive. Supported: .zip, .rar, .7z, .tar, .tar.gz, .tgz, .gz." });

                var baseName = Path.GetFileName(req.Path);
                foreach (var suffix in new[] { ".tar.gz", ".tgz", ".zip", ".tar", ".gz", ".rar", ".7z" })
                    if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { baseName = baseName[..^suffix.Length]; break; }

                // No destination = extract right next to the archive (no wrapper folder).
                var target = req.Dest;
                if (string.IsNullOrWhiteSpace(target)) target = parent;
                else if (!Path.IsPathRooted(target)) target = Path.Combine(parent, target);

                Directory.CreateDirectory(target);
                // .NET refuses entries that escape the target dir (zip-slip) and throws on existing files.
                switch (kind)
                {
                    case "zip":
                        ZipFile.ExtractToDirectory(req.Path, target, overwriteFiles: req.Overwrite);
                        break;
                    case "tar":
                        await using (var fs = System.IO.File.OpenRead(req.Path))
                            await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(fs, target, overwriteFiles: req.Overwrite);
                        break;
                    case "tgz":
                        await using (var fs = System.IO.File.OpenRead(req.Path))
                        await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                            await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gz, target, overwriteFiles: req.Overwrite);
                        break;
                    case "rar":
                    case "7z":
                        ExtractWithSharpCompress(req.Path, target, req.Overwrite);
                        break;
                    case "gz":
                        var outFile = Path.Combine(target, baseName);
                        await using (var fs = System.IO.File.OpenRead(req.Path))
                        await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                        await using (var o = new FileStream(outFile, req.Overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write))
                            await gz.CopyToAsync(o);
                        break;
                }
                return Ok(new { ok = true, created = target });
            }
            catch (IOException ex) when (!req.Overwrite && (ex.Message.Contains("already exists") || ex.HResult == unchecked((int)0x80070050) || ex.HResult == unchecked((int)0x800700B7)))
            {
                return Conflict(new { error = "Some files already exist in the destination.", conflict = true });
            }
            catch (InvalidDataException) { return BadRequest(new { error = "The archive is corrupt or not a valid archive of that type." }); }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        /// <summary>Extracts .rar / .7z. Each entry path is checked to stay inside the target folder, and existing files are never overwritten.</summary>
        private static void ExtractWithSharpCompress(string archivePath, string target, bool overwrite)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)) + Path.DirectorySeparatorChar;
            using var archive = SharpCompress.Archives.ArchiveFactory.OpenArchive(archivePath);
            if (archive.Entries.Any(e => e.IsEncrypted))
                throw new InvalidOperationException("The archive is password-protected, which is not supported.");

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Key)) continue;
                var dest = Path.GetFullPath(Path.Combine(root, entry.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Archive entry escapes the target folder: {entry.Key}");

                if (entry.IsDirectory) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = entry.OpenEntryStream();
                using var dst = new FileStream(dest, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write); // CreateNew throws if it exists
                src.CopyTo(dst);
            }
        }

        // ---------- properties ----------

        [HttpGet("props")]
        public IActionResult Props(string path)
        {
            try
            {
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                if (!info.Exists) return BadRequest(new { error = "Not found." });

                var isDir = info is DirectoryInfo;
                long size = 0; int files = 0, folders = 0;
                if (isDir)
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var e in ((DirectoryInfo)info).EnumerateFileSystemInfos("*", opts))
                    {
                        if (e is FileInfo f) { files++; size += f.Length; }
                        else folders++;
                    }
                }
                else size = ((FileInfo)info).Length;

                return Ok(new
                {
                    name = info.Name,
                    path = info.FullName,
                    type = isDir ? "Folder" : (string.IsNullOrEmpty(info.Extension) ? "File" : info.Extension.TrimStart('.').ToUpperInvariant() + " file"),
                    location = isDir ? ((DirectoryInfo)info).Parent?.FullName : ((FileInfo)info).DirectoryName,
                    size,
                    files,
                    folders,
                    created = info.CreationTime,
                    modified = info.LastWriteTime,
                    accessed = info.LastAccessTime,
                    attrs = info.Attributes.ToString(),
                    readOnly = info.Attributes.HasFlag(FileAttributes.ReadOnly),
                    hidden = info.Attributes.HasFlag(FileAttributes.Hidden),
                    target = info.LinkTarget
                });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpPost("attrs")]
        public IActionResult Attrs(AttrRequest req)
        {
            try
            {
                var a = System.IO.File.GetAttributes(req.Path);
                if (req.ReadOnly is bool ro) a = ro ? a | FileAttributes.ReadOnly : a & ~FileAttributes.ReadOnly;
                if (req.Hidden is bool h) a = h ? a | FileAttributes.Hidden : a & ~FileAttributes.Hidden;
                System.IO.File.SetAttributes(req.Path, a);
                return Ok(new { ok = true });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---------- open / download ----------

        // Extensions shown as plain text in the browser (html/xml are deliberately text, never rendered).
        private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".log", ".ini", ".cfg", ".conf", ".config", ".csv", ".json", ".yml", ".yaml", ".xml", ".html", ".htm",
            ".css", ".js", ".ts", ".jsx", ".tsx", ".cs", ".cshtml", ".razor", ".py", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs",
            ".php", ".rb", ".sql", ".bat", ".cmd", ".ps1", ".sh", ".sln", ".csproj", ".props", ".targets", ".gitignore", ".env", ".tsv", ".properties"
        };

        /// <summary>
        /// inline=true: show in the browser tab when the type is safe to view (image, pdf, audio, video, text);
        /// anything else falls back to a normal download. inline=false always downloads.
        /// </summary>
        [HttpGet("download")]
        public IActionResult Download(string path, bool inline = false)
        {
            if (!System.IO.File.Exists(path)) return NotFound();
            var ext = Path.GetExtension(path);
            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(path, out var ct)) ct = "application/octet-stream";

            var view = false;
            if (inline)
            {
                if (TextExts.Contains(ext)) { ct = "text/plain; charset=utf-8"; view = true; }
                else if (ct.StartsWith("image/") || ct.StartsWith("audio/") || ct.StartsWith("video/") || ct == "application/pdf") view = true;
            }

            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // Files are user content from the disk: stop the browser sniffing/running anything (e.g. scripts inside an .svg).
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (ct != "application/pdf") Response.Headers["Content-Security-Policy"] = "sandbox"; // sandbox breaks Chrome's PDF viewer
                return view
                    ? File(stream, ct, enableRangeProcessing: true)
                    : File(stream, ct, Path.GetFileName(path), enableRangeProcessing: true);
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---------- upload (client PC -> server) ----------

        /// <summary>
        /// Saves one uploaded file into folder `dest`. `rel` may contain sub-folders ("photos/a.jpg") to keep folder structure.
        /// Existing names are never overwritten; the new file gets a " - Copy" suffix instead.
        /// </summary>
        [HttpPost("upload")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
        public async Task<IActionResult> Upload([FromQuery] string dest, [FromQuery] string? rel)
        {
            try
            {
                if (!Directory.Exists(dest)) return BadRequest(new { error = "Destination folder not found." });
                var file = Request.Form.Files.FirstOrDefault();
                if (file == null) return BadRequest(new { error = "No file received." });

                var parts = (string.IsNullOrWhiteSpace(rel) ? file.FileName : rel)
                    .Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Any(p => !ValidName(p)))
                    return BadRequest(new { error = "Invalid file name." });

                var root = Path.GetFullPath(dest);
                var dir = root;
                foreach (var p in parts.Take(parts.Length - 1))
                {
                    dir = Path.Combine(dir, p);
                    Directory.CreateDirectory(dir);
                }
                if (!IsSameOrInside(dir, root)) return BadRequest(new { error = "Invalid path." });

                var target = Unique(Path.Combine(dir, parts[^1]), false);
                await using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await file.CopyToAsync(fs);
                return Ok(new { ok = true, saved = target });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        /// <summary>
        /// Chunked upload: the client sends a file as a series of small raw-body requests so that request-size limits
        /// in IIS / proxies / firewalls never apply. Chunks are appended to a hidden ".{id}.ldbpart" file, which is
        /// renamed to the real name when the last chunk arrives.
        /// </summary>
        [HttpPost("upload-chunk")]
        public async Task<IActionResult> UploadChunk([FromQuery] string dest, [FromQuery] string rel, [FromQuery] string id,
            [FromQuery] long offset, [FromQuery] bool last, [FromQuery] bool abort = false)
        {
            string? part = null;
            try
            {
                if (!Directory.Exists(dest)) return BadRequest(new { error = "Destination folder not found." });
                if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return BadRequest(new { error = "Bad upload id." });

                var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Any(p => !ValidName(p))) return BadRequest(new { error = "Invalid file name." });

                var root = Path.GetFullPath(dest);
                var dir = root;
                foreach (var p in parts.Take(parts.Length - 1)) dir = Path.Combine(dir, p);
                if (!IsSameOrInside(dir, root)) return BadRequest(new { error = "Invalid path." });
                Directory.CreateDirectory(dir);

                part = Path.Combine(dir, "." + id + ".ldbpart");
                if (abort) { if (System.IO.File.Exists(part)) System.IO.File.Delete(part); return Ok(new { ok = true }); }

                if (offset == 0) { /* start fresh */ }
                else if (!System.IO.File.Exists(part) || new FileInfo(part).Length != offset)
                    return Conflict(new { error = "Upload chunk out of order. Please retry the upload." });

                await using (var fs = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await Request.Body.CopyToAsync(fs);

                if (!last) return Ok(new { ok = true });
                var target = Unique(Path.Combine(dir, parts[^1]), false);
                System.IO.File.Move(part, target);
                return Ok(new { ok = true, saved = target });
            }
            catch (Exception ex)
            {
                if (part != null) try { System.IO.File.Delete(part); } catch { }
                return BadRequest(new { error = ex.Message });
            }
        }

        // ---------- multi-file / folder download (server -> client PC) ----------

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string[] Paths, DateTime Expires)> ZipTokens = new();

        /// <summary>Step 1: remember what to zip and hand back a short token (a GET URL can't carry a long path list through IIS).</summary>
        [HttpPost("zipdl")]
        public IActionResult ZipDlPrepare(PathsRequest req)
        {
            if (req.Paths.Length == 0) return BadRequest(new { error = "Nothing selected." });
            var missing = req.Paths.FirstOrDefault(p => !Exists(p));
            if (missing != null) return BadRequest(new { error = $"Not found: {missing}" });

            foreach (var kv in ZipTokens.Where(k => k.Value.Expires < DateTime.UtcNow).ToList()) ZipTokens.TryRemove(kv.Key, out _);
            var token = Guid.NewGuid().ToString("N");
            ZipTokens[token] = (req.Paths, DateTime.UtcNow.AddMinutes(5));
            return Ok(new { token });
        }

        /// <summary>Step 2: build the zip in a temp file (deleted when the response ends) and send it as a download.</summary>
        [HttpGet("zipdl")]
        public IActionResult ZipDlGet(string t)
        {
            if (!ZipTokens.TryGetValue(t, out var entry) || entry.Expires < DateTime.UtcNow) return NotFound("Download link expired. Try again.");
            try
            {
                var tmp = new FileStream(Path.Combine(Path.GetTempPath(), "ldb-" + Guid.NewGuid().ToString("N") + ".zip"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
                using (var zip = new ZipArchive(tmp, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var p in entry.Paths)
                    {
                        var full = Path.GetFullPath(p);
                        var name = Path.GetFileName(full.TrimEnd('\\', '/'));
                        if (Directory.Exists(full)) AddDir(zip, full, name, "");
                        else zip.CreateEntryFromFile(full, name, CompressionLevel.Optimal);
                    }
                }
                tmp.Position = 0;
                var zipName = entry.Paths.Length == 1 ? Path.GetFileName(entry.Paths[0].TrimEnd('\\', '/')) : "files";
                if (string.IsNullOrEmpty(zipName)) zipName = "files";
                return File(tmp, "application/zip", zipName + ".zip");
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        // ---------- text editing ----------

        private const long MaxEditBytes = 5 * 1024 * 1024;

        [HttpGet("text")]
        public IActionResult ReadText(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return BadRequest(new { error = "File not found." });
                if (fi.Length > MaxEditBytes) return BadRequest(new { error = $"File is too large to edit ({fi.Length / 1024 / 1024} MB, limit {MaxEditBytes / 1024 / 1024} MB)." });

                byte[] bytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream()) { fs.CopyTo(ms); bytes = ms.ToArray(); }

                var (enc, bomLen, encName) = DetectEncoding(bytes);
                if (enc == null) return BadRequest(new { error = "This looks like a binary file and cannot be edited as text." });

                var text = enc.GetString(bytes, bomLen, bytes.Length - bomLen);
                var eol = text.Contains("\r\n") ? "crlf" : "lf";
                return Ok(new
                {
                    name = fi.Name,
                    content = text,
                    encoding = encName,
                    eol,
                    version = fi.LastWriteTimeUtc.Ticks.ToString(),
                    readOnly = fi.IsReadOnly
                });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        public record SaveRequest(string Path, string Content, string Encoding, string Eol, string? Version, bool Force);

        [HttpPost("save")]
        public IActionResult SaveText(SaveRequest req)
        {
            try
            {
                var fi = new FileInfo(req.Path);
                if (!fi.Exists) return BadRequest(new { error = "File no longer exists." });
                if (fi.IsReadOnly) return BadRequest(new { error = "File is read-only. Clear Read-only in Properties first." });
                if (!req.Force && req.Version != null && fi.LastWriteTimeUtc.Ticks.ToString() != req.Version)
                    return Conflict(new { error = "The file was changed on disk since you opened it.", conflict = true });

                // Editor sends "\n" line breaks; restore the file's original line ending style.
                var text = req.Content.Replace("\r\n", "\n");
                if (req.Eol == "crlf") text = text.Replace("\n", "\r\n");

                var enc = EncodingFor(req.Encoding);
                var preamble = enc.GetPreamble();
                var body = enc.GetBytes(text);
                using (var fs = new FileStream(req.Path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(preamble, 0, preamble.Length);
                    fs.Write(body, 0, body.Length);
                }
                fi.Refresh();
                return Ok(new { ok = true, version = fi.LastWriteTimeUtc.Ticks.ToString() });
            }
            catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
        }

        private static Encoding EncodingFor(string name) => name switch
        {
            "utf-8-bom" => new UTF8Encoding(true),
            "utf-16le" => new UnicodeEncoding(false, true),
            "utf-16be" => new UnicodeEncoding(true, true),
            "windows-1252" => CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? new UTF8Encoding(false),
            _ => new UTF8Encoding(false)
        };

        /// <summary>Returns (encoding, BOM length, name), or encoding=null for binary data.</summary>
        private static (Encoding? enc, int bom, string name) DetectEncoding(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(true), 3, "utf-8-bom");
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, true), 2, "utf-16le");
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, true), 2, "utf-16be");
            var probe = Math.Min(b.Length, 8000);
            for (var i = 0; i < probe; i++) if (b[i] == 0) return (null, 0, "");
            try
            {
                new UTF8Encoding(false, true).GetString(b); // throws if not valid UTF-8
                return (new UTF8Encoding(false), 0, "utf-8");
            }
            catch (DecoderFallbackException)
            {
                return (CodePagesEncodingProvider.Instance.GetEncoding(1252), 0, "windows-1252"); // legacy ANSI files
            }
        }

        // ---------- helpers ----------

        private IActionResult Result(List<string> errors) =>
            errors.Count == 0 ? Ok(new { ok = true }) : StatusCode(207, new { ok = false, errors });

        private static bool Exists(string p) => Directory.Exists(p) || System.IO.File.Exists(p);

        private static bool ValidName(string? n) =>
            !string.IsNullOrWhiteSpace(n) && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && n != "." && n != "..";

        private static bool SamePath(string? a, string? b) =>
            a != null && b != null &&
            string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        private static bool IsSameOrInside(string child, string parent)
        {
            var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child)) + Path.DirectorySeparatorChar;
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameRoot(string a, string b) =>
            string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns path, or "name - Copy", "name - Copy (2)" ... if it already exists.</summary>
        private static string Unique(string path, bool isDir)
        {
            if (!Exists(path)) return path;
            var dir = Path.GetDirectoryName(path)!;
            var name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            var ext = isDir ? "" : Path.GetExtension(path);
            for (var i = 1; ; i++)
            {
                var cand = Path.Combine(dir, $"{name} - Copy{(i > 1 ? $" ({i})" : "")}{ext}");
                if (!Exists(cand)) return cand;
            }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.EnumerateFiles(src))
                System.IO.File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
            foreach (var d in Directory.EnumerateDirectories(src))
            {
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // don't follow junctions/symlinks
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
            }
        }

        private static void AddDir(ZipArchive zip, string dir, string entryName, string zipPath)
        {
            var any = false;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                any = true;
                if (string.Equals(Path.GetFullPath(f), zipPath, StringComparison.OrdinalIgnoreCase)) continue;
                zip.CreateEntryFromFile(f, entryName + "/" + Path.GetFileName(f), CompressionLevel.Optimal);
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                any = true;
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                AddDir(zip, d, entryName + "/" + Path.GetFileName(d), zipPath);
            }
            if (!any) zip.CreateEntry(entryName + "/"); // keep empty folders
        }
    }
}
