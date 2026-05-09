using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using PhigrosLibraryCSharp.CloudSave;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhigrosSaveDumper;
public static class SaveExpansion
{
	private record struct FileTokenMeta(string _checksum, string prefix, int size);
	private record struct FileTokenInfo(
		string bucket,
		string createdAt,
		string key,
		FileTokenMeta metaData,
		string mime_type,
		string name,
		string objectId,
		string provider,
		string token,
		string upload_url,
		string url);
	private record struct CreateUploadResponse(string uploadId, object expireAt);
	private record struct RequestUploadPart(string etag, string md5);
	private record struct S2CRequestFileUploadComplete(string hash, string key);

	private static JsonSerializerOptions _options = new() { PropertyNamingPolicy = null };

	public static async Task<JObject> FetchRawSaveAsNode(Save save)
	{
		HttpClient client = save.Client;
		HttpResponseMessage response = await client.GetAsync(@"https://rak3ffdi.cloud.tds1.tapapis.cn/1.1/classes/_GameSave");
		string content = await response.Content.ReadAsStringAsync();
		return JObject.Parse(content);
	}

	public static async Task UploadSave(Save save, string userObjectId, string? oldSaveGameFileObjectId, string? oldSaveObjectId, byte[] packedSaveBuffer, byte[] packedSummaryBuffer)
	{
		FileTokenInfo token = await CreateFileToken(packedSaveBuffer, userObjectId, save);
		CreateUploadResponse uploadInfo = await CreateUpload(token, save);

		(int, RequestUploadPart)[] parts = [(1, await UploadPart(1, packedSaveBuffer, token, uploadInfo, save))];
		await CompleteUpload(token, uploadInfo, save, parts);
		await UpdateSummary(token, packedSummaryBuffer, oldSaveObjectId, userObjectId, save);
		if (oldSaveGameFileObjectId is not null)
			await DeleteOld(oldSaveGameFileObjectId, save);
	}

	private static async Task<FileTokenInfo> CreateFileToken(byte[] packedSaveBuffer, string userObjectId, Save save)
	{
		HttpClient client = save.Client;

		var fileTokenRequest = new
		{
			name = ".save",
			__type = "File",
			ACL = new Dictionary<string, object>(),
			prefix = "gamesaves",
			metaData = new
			{
				size = packedSaveBuffer.Length,
				_checksum = Convert.ToHexString(MD5.HashData(packedSaveBuffer)),
				prefix = "gamesaves"
			}
		};
		fileTokenRequest.ACL["userObjectId"] = new
		{
			read = true,
			write = true
		};

		HttpResponseMessage response = await client.PostAsync($"{Save.CloudServerAddress}/1.1/fileTokens", JsonContent.Create(fileTokenRequest, options: _options));
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadFromJsonAsync<FileTokenInfo>();
	}
	private static async Task<CreateUploadResponse> CreateUpload(FileTokenInfo info, Save save)
	{
		HttpClient client = save.Client;
		HttpRequestMessage request = new(
			HttpMethod.Post,
			$"https://upload.qiniup.com/buckets/rAK3Ffdi/objects/{Convert.ToBase64String(Encoding.UTF8.GetBytes(info.key))}/uploads")
		{
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("UpToken", info.token);
		HttpResponseMessage response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadFromJsonAsync<CreateUploadResponse>();
	}
	private static async Task<RequestUploadPart> UploadPart(int partNumber, byte[] packedSaveBuffer, FileTokenInfo info, CreateUploadResponse uploadCreation, Save save)
	{
		HttpClient client = save.Client;
		HttpRequestMessage request = new(
			HttpMethod.Put,
			$"https://upload.qiniup.com/buckets/rAK3Ffdi/objects/{Convert.ToBase64String(Encoding.UTF8.GetBytes(info.key))}/uploads/{uploadCreation.uploadId}/{partNumber}")
		{
			Content = new ByteArrayContent(packedSaveBuffer)
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("UpToken", info.token);
		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
		HttpResponseMessage response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();

		return await response.Content.ReadFromJsonAsync<RequestUploadPart>();
	}
	private static async Task CompleteUpload(FileTokenInfo info, CreateUploadResponse uploadCreation, Save save, params (int Index, RequestUploadPart Part)[] parts)
	{
		HttpClient client = save.Client;
		HttpRequestMessage request1 = new(HttpMethod.Post, $"https://upload.qiniup.com/buckets/rAK3Ffdi/objects/" +
			$"{Convert.ToBase64String(Encoding.UTF8.GetBytes(info.key))}/uploads/{uploadCreation.uploadId}")
		{
			Content = JsonContent.Create(new { parts = parts.Select(x => new { partNumber = x.Index, x.Part.etag }).ToArray() }, options: _options)
		};
		request1.Headers.Authorization = new AuthenticationHeaderValue("UpToken", info.token);
		HttpResponseMessage response1 = await client.SendAsync(request1);
		response1.EnsureSuccessStatusCode();

		HttpRequestMessage request2 = new(HttpMethod.Post, @"https://rak3ffdi.cloud.tds1.tapapis.cn/1.1/fileCallback")
		{
			Content = JsonContent.Create(new
			{
				result = true,
				token = Convert.ToHexString(Encoding.UTF8.GetBytes(info.key))
			}, options: _options)
		};
		HttpResponseMessage response2 = await client.SendAsync(request2);
		response2.EnsureSuccessStatusCode();
	}
	private static async Task UpdateSummary(FileTokenInfo info, byte[] packedSummaryData, string? oldSaveObjectId, string userObjectId, Save save)
	{
		HttpClient client = save.Client;

		var requestData = new
		{
			summary = Convert.ToBase64String(packedSummaryData),
			modifiedAt = new
			{
				__type = "Date",
				iso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.FFFZ", CultureInfo.InvariantCulture)
			},
			gameFile = new
			{
				__type = "Pointer",
				className = "_File",
				info.objectId
			},
			ACL = new Dictionary<string, object>(),
			user = new
			{
				__type = "Pointer",
				className = "_User",
				objectId = userObjectId
			}
		};
		requestData.ACL[userObjectId] = new
		{
			read = true,
			write = true,
		};
		string url = @"https://rak3ffdi.cloud.tds1.tapapis.cn/1.1/classes/_GameSave";
		HttpMethod method = HttpMethod.Put;
		if (!string.IsNullOrEmpty(oldSaveObjectId))
		{
			url += $"/{oldSaveObjectId}";
			method = HttpMethod.Post;
		}
		HttpRequestMessage request = new(method, url)
		{
			Content = JsonContent.Create(requestData, options: _options)
		};
		HttpResponseMessage response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();
	}
	public static async Task DeleteOld(string oldSaveGameFileObjectId, Save save)
	{
		HttpClient client = save.Client;
		HttpResponseMessage response = await client.DeleteAsync($"https://rak3ffdi.cloud.tds1.tapapis.cn/1.1/files/{oldSaveGameFileObjectId}");
		response.EnsureSuccessStatusCode();
	}

	internal static async Task UnpackRawZip(this Save helper, int saveIndex, DirectoryInfo target, ILogger logger)
	{
		byte[] rawZip = await helper.GetSaveZipAsync((await helper.GetSaveInfoFromCloudAsync()).GetParsedSaves()[saveIndex]);

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

		logger.LogInformation("Writing raw zip, size: {size} KiB.", rawZip.Length / 1024f);
		FileStream fileStream = File.Open(Path.Combine(encryptedDir.FullName, ".cloud.zip"), FileMode.Create, FileAccess.ReadWrite);
		fileStream.Write(rawZip);
		fileStream.Seek(0, SeekOrigin.Begin);
		logger.LogInformation("Writing raw zip done.");
		using ZipFile zipFile = new(fileStream);
		foreach (ZipEntry recordFile in zipFile)
		{
			logger.LogInformation("Processing record '{recordName}'.", recordFile.Name);
			byte[] decompressed = new byte[recordFile.Size];
			zipFile.GetInputStream(recordFile).Read(decompressed, 0, decompressed.Length);
			File.WriteAllBytes(Path.Combine(encryptedDir.FullName, recordFile.Name), decompressed);
			logger.LogInformation("Wrote encrypted record '{recordName}'.", recordFile.Name);
			decompressed = decompressed[1..]; // for some reason i need to trim the first byte

			//byte[] decrypted = await this.Runtime!.InvokeAsync<byte[]>("AesDecrypt", decompressed, key, iv); 
			byte[] decrypted = await helper.Decrypt(decompressed);
			logger.LogInformation("Wrote decrypted record '{recordName}'.", recordFile.Name);
			File.WriteAllBytes(Path.Combine(decryptedDir.FullName, recordFile.Name), decrypted);
		}
		logger.LogInformation("Wrote all record.");
	}
}
