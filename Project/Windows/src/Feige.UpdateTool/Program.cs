using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feige.Core.Updates;

try
{
    if (args.Length == 2 && args[0] is "keygen" or "public-key")
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("CNG 发布签名密钥只在 Windows 发布环境创建和使用。");
        string name = args[1];
        if (!Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,100}$")) throw new InvalidDataException("签名密钥名称无效。");
        var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        if (args[0] == "keygen")
        {
            if (CngKey.Exists(name, provider)) throw new InvalidOperationException("同名签名密钥已存在，不会覆盖。");
            var parameters = new CngKeyCreationParameters { Provider = provider, KeyUsage = CngKeyUsages.Signing, ExportPolicy = CngExportPolicies.None };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));
            using var created = CngKey.Create(CngAlgorithm.Rsa, name, parameters);
        }
        using var key = CngKey.Open(name, provider);
        using var rsa = new RSACng(key);
        Console.WriteLine(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    if (args.Length == 3 && args[0] == "verify")
    {
        // Accept either the multi-origin configuration or a single origin: each configured origin
        // gets its own manifest, because download URLs are bound to the origin that served them.
        var sources = ReadSources(args[1]);
        var bytes = File.ReadAllBytes(args[2]);
        UpdateManifest? manifest = null;
        Exception? last = null;
        foreach (var source in sources)
        {
            try { manifest = UpdateManifest.Verify(bytes, source); break; }
            catch (InvalidDataException ex) { last = ex; }
        }
        if (manifest is null) throw last ?? new InvalidDataException("更新清单与任何配置来源都不匹配。");
        Console.WriteLine(JsonSerializer.Serialize(new { releases = manifest.Releases }, UpdateManifest.Json));
        return 0;
    }
    if ((args.Length == 7 || args.Length == 9) && args[0] == "sign")
    {
        // A PKCS#8 key file allows signing on machines whose CNG store cannot hold the key
        // (for example a Parallels guest); the CNG path stays the default.
        string? keyFilePath = null;
        if (args.Length == 9)
        {
            if (!string.Equals(args[7], "--key-file", StringComparison.Ordinal)) throw new InvalidDataException("未知的签名选项。");
            keyFilePath = Path.GetFullPath(args[8]);
        }
        else if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("发布签名必须在拥有 CNG 私钥的 Windows 用户环境执行，或改用 --key-file 指定 PKCS#8 私钥文件。");
        }
        string root = Path.GetFullPath(args[1]), sourcePath = Path.GetFullPath(args[2]), keyName = args[3], keyId = args[4];
        string input = Path.GetFullPath(args[5]), output = Path.GetFullPath(args[6]);
        // The signed manifest is produced in the ignored build staging folder and archived
        // into download/ by Publish-LanUpdate.ps1 once the LAN upload succeeds.
        string builds = Path.Combine(root, "Windows", "dist", "build") + Path.DirectorySeparatorChar;
        if (!File.Exists(Path.Combine(root, "AGENTS.md")) || !output.StartsWith(builds, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("清单输出必须位于 Windows/dist/build 中。");
        var sources = ReadSources(sourcePath);
        var source = sources.FirstOrDefault(s => s.PublicKeys.ContainsKey(keyId))
            ?? throw new InvalidDataException("签名公钥标识尚未配置到客户端。");
        byte[] payload = File.ReadAllBytes(input);
        if (payload.Length > UpdateManifest.MaximumPayloadBytes) throw new InvalidDataException("发布记录过大。");
        using var rsa = keyFilePath is null ? OpenCngKey(keyName) : OpenPkcs8Key(keyFilePath);
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new { format = 1, keyId, payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(rsa.SignData(UpdateManifest.SigningBytes(keyId, payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) });
        var manifest = UpdateManifest.Verify(envelope, source);
        if (manifest.Releases.Count == 0) throw new InvalidDataException("发布记录没有安装包。");
        foreach (var release in manifest.Releases)
        {
            if (release.Channel != source.Channel || release.Installation != "exe") throw new InvalidDataException("发布渠道或安装类型不匹配。");
            string package = Path.Combine(builds, Path.GetFileName(new Uri(release.DownloadUrl).LocalPath));
            using var file = File.OpenRead(package);
            if (file.Length != release.Size || !Convert.ToHexString(SHA256.HashData(file)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("发布记录与最终安装包字节不一致。");
            using var metadata = JsonDocument.Parse(File.ReadAllText(package + ".json"));
            if (metadata.RootElement.GetProperty("version").GetString() != release.Version ||
                metadata.RootElement.GetProperty("build").GetInt64() != release.Build ||
                metadata.RootElement.GetProperty("architecture").GetString() != release.Architecture ||
                !metadata.RootElement.GetProperty("signed").GetBoolean())
                throw new InvalidDataException("发布记录与安装包构建信息不一致。");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(envelope); file.Flush(flushToDisk: true); }
        Console.WriteLine("更新清单签名和验证成功。");
        return 0;
    }
    Console.Error.WriteLine("Usage: keygen NAME | public-key NAME | verify SOURCE MANIFEST | sign ROOT SOURCE KEY_NAME KEY_ID RECORD OUTPUT [--key-file PKCS8_PATH]");
    return 2;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"更新发布工具失败：{ex.GetType().Name}。请检查参数、签名环境和发布记录；不会继续发布。");
    return 1;
}

/// <summary>Opens the non-exportable CNG manifest signing key.</summary>
static RSA OpenCngKey(string keyName)
{
    var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
    return new RSACng(key);
}

/// <summary>Loads a base64 PKCS#8 RSA private key from a file, failing closed on anything else.</summary>
static RSA OpenPkcs8Key(string path)
{
    if (!File.Exists(path)) throw new InvalidDataException("未找到清单私钥文件。");
    if (new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("清单私钥文件过大。");
    byte[] der;
    try { der = Convert.FromBase64String(File.ReadAllText(path).Trim()); }
    catch (FormatException) { throw new InvalidDataException("清单私钥文件不是有效的 base64 PKCS#8 内容。"); }
    var rsa = RSA.Create();
    try
    {
        rsa.ImportPkcs8PrivateKey(der, out int read);
        if (read != der.Length) throw new InvalidDataException("清单私钥文件包含多余数据。");
        if (rsa.KeySize < 3072) throw new InvalidDataException("清单私钥必须至少 3072 位。");
        return rsa;
    }
    catch
    {
        rsa.Dispose();
        throw;
    }
}

static IReadOnlyList<UpdateSource> ReadSources(string path)
{
    // File.ReadAllText handles a UTF-8 BOM written by Windows PowerShell.
    string json = File.ReadAllText(path);
    try
    {
        var sources = JsonSerializer.Deserialize<UpdateSources>(json, UpdateManifest.Json) ?? throw new InvalidDataException("更新来源为空。");
        sources.Validate();
        return sources.Resolve();
    }
    catch (JsonException)
    {
        // Fall back to a legacy single-origin file.
        var source = JsonSerializer.Deserialize<UpdateSource>(json, UpdateManifest.Json) ?? throw new InvalidDataException("更新来源为空。");
        source.Validate();
        return [source.Normalize()];
    }
}
