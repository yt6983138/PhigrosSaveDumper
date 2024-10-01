using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PhigrosLibraryCSharp;
using System.IO;
using yt6983138.Common;

namespace PhigrosSaveDumper;
internal static class Extension
{
	internal static async Task UnpackRawZip(this Save helper, int saveIndex, DirectoryInfo target, Logger logger, EventId eventId)
	{
		byte[] rawZip = await helper.GetSaveRawZipAsync((await helper.GetRawSaveFromCloudAsync()).GetParsedSaves()[saveIndex]);

		const string EncryptedFolder = "Encrypted";
		const string DecryptedFolder = "Decrypted";
		if (!target.Exists)
		{
			target.Create();
		}
		DirectoryInfo encryptedDir = target.GetDirectories()
			.FirstOrDefault(x => x.Name == EncryptedFolder) ?? target.CreateSubdirectory(EncryptedFolder);
		DirectoryInfo decryptedDir = target.GetDirectories()
			.FirstOrDefault(x => x.Name == DecryptedFolder) ?? target.CreateSubdirectory(DecryptedFolder);

		logger.Log<MainWindow>(LogLevel.Information, $"Writing raw zip, size: {rawZip.Length / 1024f} KiB.", eventId, null!);
		FileStream fileStream = File.Open(Path.Combine(encryptedDir.FullName, ".cloud.zip"), FileMode.Create, FileAccess.ReadWrite);
		fileStream.Write(rawZip);
		fileStream.Seek(0, SeekOrigin.Begin);
		logger.Log<MainWindow>(LogLevel.Information, $"Writing raw zip done.", eventId, null!);

		using ZipFile zipFile = new(fileStream);
		foreach (ZipEntry recordFile in zipFile)
		{
			logger.Log<MainWindow>(LogLevel.Information, $"Processing record '{recordFile.Name}'.", eventId, null!);
			byte[] decompressed = new byte[recordFile.Size];
			zipFile.GetInputStream(recordFile).Read(decompressed, 0, decompressed.Length);
			File.WriteAllBytes(Path.Combine(encryptedDir.FullName, recordFile.Name), decompressed);
			logger.Log<MainWindow>(LogLevel.Information, $"Wrote encrypted record '{recordFile.Name}'.", eventId, null!);
			decompressed = decompressed[1..]; // for some reason i need to trim the first byte

			//byte[] decrypted = await this.Runtime!.InvokeAsync<byte[]>("AesDecrypt", decompressed, key, iv); 
			byte[] decrypted = await helper.Decrypt(decompressed);
			logger.Log<MainWindow>(LogLevel.Information, $"Wrote decrypted record '{recordFile.Name}'.", eventId, null!);
			File.WriteAllBytes(Path.Combine(decryptedDir.FullName, recordFile.Name), decrypted);
		}
		logger.Log<MainWindow>(LogLevel.Information, $"Wrote all record.", eventId, null!);
	}
	internal static string ToJson<T>(this T obj)
	{
		return JsonConvert.SerializeObject(obj, Formatting.Indented);
	}
}
