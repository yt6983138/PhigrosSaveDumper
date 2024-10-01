using Microsoft.Extensions.Logging;
using PhigrosLibraryCSharp;
using PhigrosLibraryCSharp.Cloud.Login;
using PhigrosLibraryCSharp.Cloud.Login.DataStructure;
using PhigrosLibraryCSharp.Extensions;
using PhigrosLibraryCSharp.GameRecords;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using yt6983138.Common;

namespace PhigrosSaveDumper;
/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
	private Save? _saveHelper;

	internal readonly static EventId TapTapEventId = new(0, "TapTap");
	internal readonly static EventId MiscEventId = new(0, "Misc");
	internal readonly static EventId OperationEventId = new(0, "Operations");

	internal Logger Logger { get; set; } = new("./latest.log");
	public Save? SaveHelper
	{
		get => this._saveHelper;
		set
		{
			this._saveHelper = value;
			OnLockOrUnlock?.Invoke(this, value);
		}
	}
	internal event EventHandler<Save?>? OnLockOrUnlock;

	public MainWindow()
	{
		this.InitializeComponent();
		this.Logger.OnAfterLog += (_, _2) => this.LogOutput.Text = string.Join("", Logger.AllLogs);
		OnLockOrUnlock += (_, obj) => this.OperationGroupBox.IsEnabled = obj is not null;
		OnLockOrUnlock += (_, obj) => this.SettingsGroupBox.IsEnabled = obj is not null;
		this.SaveHelper = null;

		if (!Directory.Exists("./Saves/"))
			Directory.CreateDirectory("./Saves/");
	}
	public async void LoginTapTap(object _, RoutedEventArgs _2)
	{
		this.Logger.Log(LogLevel.Information, "TapTap login started...", TapTapEventId, this);
		try
		{
			this.Logger.Log(LogLevel.Information, "Requesting login link...", TapTapEventId, this);
			CompleteQRCodeData qrCode = await TapTapHelper.RequestLoginQrCode();
			Process.Start(new ProcessStartInfo(qrCode.Url) { UseShellExecute = true });
			this.Logger.Log(LogLevel.Information, $"QRCode url generated: {qrCode.Url}, " +
				$"expires in {qrCode.ExpiresInSeconds} seconds.", TapTapEventId, this);
			this.ListenLogin(qrCode);
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, TapTapEventId, this, ex);
		}
	}
	public async void ListenLogin(CompleteQRCodeData data)
	{
		DateTime expiresAt = DateTime.Now + new TimeSpan(0, 0, data.ExpiresInSeconds);
		while (DateTime.Now < expiresAt)
		{
			await Task.Delay(2500);
			TapTapTokenData? result = await TapTapHelper.CheckQRCodeResult(data);
			if (result is not null)
			{
				TapTapProfileData profile = await TapTapHelper.GetProfile(result.Data);
				LCCombinedAuthData completed = new(profile.Data, result.Data);
				string token = await LCHelper.LoginAndGetToken(completed);
				this.TokenTextBox.Text = token;
				this.Logger.Log(LogLevel.Information, $"Got token: {token}", TapTapEventId, this);
				return;
			}
		}
	}
	public async void LockOrUnlock(object sender, RoutedEventArgs e)
	{
		if (!this.TokenTextBox.IsEnabled)
		{
			goto Final;
		}
		try
		{
			this.Logger.Log(LogLevel.Information, "Locking token...", MiscEventId, this);
			Save helper = new(this.TokenTextBox.Text.Trim());
			_ = await helper.GetUserInfoAsync();
			this.SaveHelper = helper;
			this.TokenTextBox.IsEnabled = false;
			this.Logger.Log(LogLevel.Information, "Token locked.", MiscEventId, this);
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, $"Error while locking:", MiscEventId, this, ex);
			goto Final;
		}
		return;
	Final:
		this.SaveHelper = null;
		this.TokenTextBox.IsEnabled = true;
		this.Logger.Log(LogLevel.Information, "Token unlocked.", MiscEventId, this);
		return;
	}

	public async void DownloadUnpackButton_Click(object sender, RoutedEventArgs e)
	{
		DirectoryInfo nativeDir = new("./Saves/Native");
		if (nativeDir.Exists)
		{
			string dateTimeString = DateTime.Now.ToString("s");
			dateTimeString = $"{dateTimeString.Replace(':', '_')},{(DateTime.Now - default(DateTime)).TotalMilliseconds}";
			try
			{
				nativeDir.MoveTo($"./Saves/{dateTimeString}");
				nativeDir = new("./Saves/Native");
				nativeDir.Create();
				this.Logger.Log(LogLevel.Information, $"Renamed folder from 'Native' to '{dateTimeString}'.", MiscEventId, this);
			}
			catch (Exception ex)
			{
				this.Logger.Log(LogLevel.Information, $"Error while moving folder:", OperationEventId, this, ex);
				return;
			}
		}
		try
		{
			await this.SaveHelper!.UnpackRawZip(this.TimeIndexSelector.Value ?? 0, nativeDir, this.Logger, OperationEventId);
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, $"Error while unpacking:", OperationEventId, this, ex);
		}
	}

	public async void ListTimeIndex_Click(object sender, RoutedEventArgs e)
	{
		int i = 0;
		string message = string.Join("\n",
				(await this.SaveHelper!.GetRawSaveFromCloudAsync())
				.GetParsedSaves()
				.Select(x => $"{i++}: {x.ModificationTime}")
			);
		MessageBox.Show(message);
		this.Logger.Log(LogLevel.Information, $"Listing indexes...\n{message}", OperationEventId, this);
	}

	private async void EmulateReadRecordButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			ByteReader byteReader = new(File.ReadAllBytes("./Saves/Native/Decrypted/gameRecord"));
			if (!File.Exists("difficulty.tsv"))
			{
				using HttpClient client = new();
				byte[] data = await client.GetByteArrayAsync(@"https://yt6983138.github.io/Assets/RksReader/Latest/difficulty.csv");
				File.WriteAllBytes("difficulty.csv", data);
			}
			string[] lines = File.ReadAllLines("difficulty.csv");
			Dictionary<string, float[]> difficulties = new();
			foreach (string line in lines)
			{
				string[] splitted = line.Replace("\n", "").Replace("\r", "").Split('\t');
				if (splitted.Length < 2) continue;
				string name = splitted[0];
				float[] diffs = splitted[1..]
					.Select(float.Parse)
					.ToArray();
				difficulties.Add(name, diffs);
			}
			List<CompleteScore> scores = byteReader.ReadAllGameRecord(difficulties, null);
			this.Logger.Log(LogLevel.Information, OperationEventId, "Got save! printing first 10...");
			for (int i = 0; i < 10; i++)
			{
				CompleteScore score = scores[i];
				this.Logger.Log(LogLevel.Information, OperationEventId, "{0}: {1}/{2}/{3}", i, score.Id, score.Score, score.Accuracy);
			}
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, $"Error while reading:", OperationEventId, this, ex);
		}
	}

	private async void EmulateReadUserInfoButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			this.Logger.Log(LogLevel.Information, "Loading user info", OperationEventId, this);
			Save saveHelper = this.SaveHelper!;

			this.Logger.Log(LogLevel.Information, (await saveHelper.GetUserInfoAsync()).ToJson(), OperationEventId, this);
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, $"Error while reading:", OperationEventId, this, ex);
		}
	}

	private void EmulateReadGameUserInfoButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			this.Logger.Log(LogLevel.Information, "Loading game user info", OperationEventId, this);
			Save saveHelper = this.SaveHelper!;

			byte[] rawData = File.ReadAllBytes("./Saves/Native/Decrypted/user"); // note raw data is zip
			ByteReader reader = new(rawData);
			this.Logger.Log(LogLevel.Information, reader.ReadGameUserInfo().ToJson(), OperationEventId, this);
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, $"Error while reading:", OperationEventId, this, ex);
		}
	}

	private void EmulateReadProgressButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			this.Logger.Log(LogLevel.Information, "Loading game progress", OperationEventId, this);
			Save saveHelper = this.SaveHelper!;

			byte[] rawData = File.ReadAllBytes("./Saves/Native/Decrypted/gameProgress"); // note raw data is zip
			ByteReader reader = new(rawData);
			this.Logger.Log(LogLevel.Information, reader.ReadGameProgress().ToJson(), OperationEventId, this);
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, $"Error while reading:", OperationEventId, this, ex);
		}
	}

	private void EmulateReadSettingsButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			this.Logger.Log(LogLevel.Information, "Loading game settings", OperationEventId, this);
			Save saveHelper = this.SaveHelper!;

			byte[] rawData = File.ReadAllBytes("./Saves/Native/Decrypted/settings"); // note raw data is zip
			ByteReader reader = new(rawData);
			this.Logger.Log(LogLevel.Information, reader.ReadGameSettings().ToJson(), OperationEventId, this);
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, $"Error while reading:", OperationEventId, this, ex);
		}
	}
	private async void EmulateGetRawInfoButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			PhigrosLibraryCSharp.Cloud.DataStructure.Raw.RawSaveContainer data = await this.SaveHelper!.GetRawSaveFromCloudAsync();
			this.Logger.Log(LogLevel.Information, data.ToJson(), OperationEventId, this);
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, $"Error while reading:", OperationEventId, this, ex);
		}
	}
	private async void DoSomethingButton_Click(object sender, RoutedEventArgs e)
	{
		Save saveHelper = this.SaveHelper!;
		HttpClient httpClient = new();
		//(HttpClient)typeof(Save).GetProperty("Client",
		//BindingFlags.NonPublic | BindingFlags.Instance)!
		//.GetValue(saveHelper)!;


		//HttpResponseMessage pRes = await httpClient.PostAsync("https://rak3ffdi.cloud.tds1.tapapis.cn/1.1/fileTokens",
		//	new StringContent("""
		//		{
		//		  "name": ".save",
		//		  "__type": "File",
		//		  "ACL": {
		//		    "65ed247be053a67229bd73ba": {
		//		      "read": true,
		//		      "write": true
		//		    }
		//		  },
		//		  "prefix": "gamesaves",
		//		  "metaData": {
		//		    "size": 13056,
		//		    "_checksum": "a43379f945e6036ace1cc5715ff08a6a",
		//		    "prefix": "gamesaves"
		//		  }
		//		}
		//		""", MediaTypeHeaderValue.Parse("application/json")));
		//HttpResponseMessage pRes = await httpClient.DeleteAsync("https://rak3ffdi.cloud.tds1.tapapis.cn/1.1/files/65ed24b4e053a67229bd752a");
		httpClient.DefaultRequestHeaders.Add("Authorization", "UpToken bOJAZVDET_Z11xes0ufp39ao_Tie7mrGqecKRkUf:SFTjLCK774trDdGvnua0BWniF2s=:eyJzY29wZSI6InJBSzNGZmRpOmdhbWVzYXZlcy9DaGNxZDlKRXZuUTVzS0lmRnB6YlFhZjlMcHU4WXcxQy8uc2F2ZSIsImRlYWRsaW5lIjoxNzI2OTMxMzAzLCJpbnNlcnRPbmx5IjoxfQ==");
		HttpResponseMessage pRes = await httpClient.PostAsync("https://rak3ffdi.cloud.tds1.tapapis.cn/1.1/fileCallback",
			new StringContent(
				"""
					{
						"result": true,
						"token": "bOJAZVDET_Z11xes0ufp39ao_Tie7mrGqecKRkUf:SFTjLCK774trDdGvnua0BWniF2s=:eyJzY29wZSI6InJBSzNGZmRpOmdhbWVzYXZlcy9DaGNxZDlKRXZuUTVzS0lmRnB6YlFhZjlMcHU4WXcxQy8uc2F2ZSIsImRlYWRsaW5lIjoxNzI2OTMxMzAzLCJpbnNlcnRPbmx5IjoxfQ=="
					}
				""", MediaTypeHeaderValue.Parse("application/json")));
		Console.WriteLine(await pRes.Content.ReadAsStringAsync());

	}
}
