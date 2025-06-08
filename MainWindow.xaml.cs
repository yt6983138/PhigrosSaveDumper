using Microsoft.Extensions.Logging;
using PhigrosLibraryCSharp;
using PhigrosLibraryCSharp.Cloud.Login;
using PhigrosLibraryCSharp.Cloud.Login.DataStructure;
using PhigrosLibraryCSharp.Extensions;
using PhigrosLibraryCSharp.GameRecords;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using yt6983138.Common;

namespace PhigrosSaveDumper;
/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
	private Save? _saveHelper;

	internal static readonly EventId TapTapEventId = new(0, "TapTap");
	internal static readonly EventId MiscEventId = new(0, "Misc");
	internal static readonly EventId OperationEventId = new(0, "Operations");

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
		this.Logger.OnAfterLog += (_, _2) =>
		{
			this.LogOutput.Text = string.Join("", Logger.AllLogs);
			this.LogOutput.ScrollToEnd();
		};
		OnLockOrUnlock += (_, obj) => this.OperationGroupBox.IsEnabled = obj is not null;
		OnLockOrUnlock += (_, obj) => this.SettingsGroupBox.IsEnabled = obj is not null;
		this.SaveHelper = null;

		if (!Directory.Exists("./Saves/"))
			Directory.CreateDirectory("./Saves/");
	}

	private async Task<bool> ExecuteHandled(Func<Task> action, string errorMessage)
	{
		try
		{
			await action.Invoke();
			return true;
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, errorMessage, OperationEventId, this, ex);
			return false;
		}
	}
	private bool ExecuteHandled(Action action, string errorMessage)
	{
		try
		{
			action.Invoke();
			return true;
		}
		catch (Exception ex)
		{
			this.Logger.Log(LogLevel.Information, errorMessage, OperationEventId, this, ex);
			return false;
		}
	}

	private async void LoginTapTap(object _, RoutedEventArgs _2)
	{
		this.Logger.Log(LogLevel.Information, "TapTap login started...", TapTapEventId, this);
		await this.ExecuteHandled(async () =>
		{
			this.Logger.Log(LogLevel.Information, "Requesting login link...", TapTapEventId, this);
			CompleteQRCodeData qrCode = await TapTapHelper.RequestLoginQrCode();
			Process.Start(new ProcessStartInfo(qrCode.Url) { UseShellExecute = true });
			this.Logger.Log(LogLevel.Information, $"QRCode url generated: {qrCode.Url}, " +
				$"expires in {qrCode.ExpiresInSeconds} seconds.", TapTapEventId, this);
			this.ListenLogin(qrCode);
		}, "Error generating qrcode:");
	}
	private async void ListenLogin(CompleteQRCodeData data)
	{
		DateTime expiresAt = DateTime.Now + new TimeSpan(0, 0, data.ExpiresInSeconds);
		while (DateTime.Now < expiresAt)
		{
			await this.ExecuteHandled(async () =>
			{
				await Task.Delay(2500);
				TapTapTokenData? result = await TapTapHelper.CheckQRCodeResult(data);
				if (result is not null)
				{
					TapTapProfileData profile = await TapTapHelper.GetProfile(result.Data);

					this.Logger.Log(LogLevel.Information, $"Login result: {System.Text.Json.JsonSerializer.Serialize(result)}", TapTapEventId, this);
					this.Logger.Log(LogLevel.Information, $"Profile result: {System.Text.Json.JsonSerializer.Serialize(profile)}", TapTapEventId, this);

					LCCombinedAuthData completed = new(profile.Data, result.Data);
					JsonNode invoked = await LCHelper.LoginWithAuthData(completed);

					this.Logger.Log(LogLevel.Information, $"Final result: {invoked.ToJsonString()}", TapTapEventId, this);

					string token = ((string?)invoked["sessionToken"]).EnsureNotNull();
					this.TokenTextBox.Text = token;
					this.Logger.Log(LogLevel.Information, $"Got token: {token}", TapTapEventId, this);
					return;
				}
			}, "Error processing login:");
		}
	}
	private async void LockOrUnlock(object sender, RoutedEventArgs e)
	{
		if (!this.TokenTextBox.IsEnabled)
		{
			goto Final;
		}
		if (!await this.ExecuteHandled(async () =>
		{
			this.Logger.Log(LogLevel.Information, "Locking token...", MiscEventId, this);
			Save helper = new(this.TokenTextBox.Text.Trim());
			_ = await helper.GetUserInfoAsync();
			this.SaveHelper = helper;
			this.TokenTextBox.IsEnabled = false;
			this.Logger.Log(LogLevel.Information, "Token locked.", MiscEventId, this);
		}, $"Error while locking:"))
		{
			goto Final;
		}

		return;
	Final:
		this.SaveHelper = null;
		this.TokenTextBox.IsEnabled = true;
		this.Logger.Log(LogLevel.Information, "Token unlocked.", MiscEventId, this);
		return;
	}

	private async void DownloadUnpackButton_Click(object sender, RoutedEventArgs e)
	{
		DirectoryInfo nativeDir = new("./Saves/Native");
		if (nativeDir.Exists)
		{
			string dateTimeString = DateTime.Now.ToString("s");
			dateTimeString = $"{dateTimeString.Replace(':', '_')},{(DateTime.Now - default(DateTime)).TotalMilliseconds}";

			if (!this.ExecuteHandled(() =>
			{
				nativeDir.MoveTo($"./Saves/{dateTimeString}");
				nativeDir = new("./Saves/Native");
				nativeDir.Create();
				this.Logger.Log(LogLevel.Information, $"Renamed folder from 'Native' to '{dateTimeString}'.", MiscEventId, this);
			}, "Error while moving folder:"))
			{
				return;
			}
		}
		await this.ExecuteHandled(async () =>
		{
			await this.SaveHelper!.UnpackRawZip(this.TimeIndexSelector.Value ?? 0, nativeDir, this.Logger, OperationEventId);
		}, "Error while unpacking:");
	}

	private async void ListTimeIndex_Click(object sender, RoutedEventArgs e)
	{
		await this.ExecuteHandled(async () =>
		{
			int i = 0;
			string message = string.Join("\n",
					(await this.SaveHelper!.GetRawSaveFromCloudAsync())
					.GetParsedSaves()
					.Select(x => $"{i++}: {x.ModificationTime}")
				);
			MessageBox.Show(message);
			this.Logger.Log(LogLevel.Information, $"Listing indexes...\n{message}", OperationEventId, this);
		}, "Error while listing index:");
	}

	private async void EmulateReadRecordButton_Click(object sender, RoutedEventArgs e)
	{
		await this.ExecuteHandled(async () =>
		{
			ByteReader byteReader = new(File.ReadAllBytes("./Saves/Native/Decrypted/gameRecord"));
			if (!File.Exists("difficulty.tsv"))
			{
				using HttpClient client = new();
				byte[] data = await client.GetByteArrayAsync(@"https://raw.githubusercontent.com/7aGiven/Phigros_Resource/refs/heads/info/difficulty.tsv");
				File.WriteAllBytes("difficulty.tsv", data);
			}
			string[] lines = File.ReadAllLines("difficulty.tsv");
			Dictionary<string, float[]> difficulties = [];
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
		}, "Error while reading:");
	}

	private async void EmulateReadUserInfoButton_Click(object sender, RoutedEventArgs e)
	{
		await this.ExecuteHandled(async () =>
		{
			this.Logger.Log(LogLevel.Information, "Loading user info", OperationEventId, this);
			Save saveHelper = this.SaveHelper!;

			this.Logger.Log(LogLevel.Information, (await saveHelper.GetUserInfoAsync()).ToJson(), OperationEventId, this);
		}, "Error while reading:");
	}

	private void EmulateReadGameUserInfoButton_Click(object sender, RoutedEventArgs e)
	{
		this.ExecuteHandled(() =>
		{
			this.Logger.Log(LogLevel.Information, "Loading game user info", OperationEventId, this);
			Save saveHelper = this.SaveHelper!;

			byte[] rawData = File.ReadAllBytes("./Saves/Native/Decrypted/user"); // note raw data is zip
			ByteReader reader = new(rawData);
			this.Logger.Log(LogLevel.Information, reader.ReadGameUserInfo().ToJson(), OperationEventId, this);
		}, "Error while reading:");
	}

	private void EmulateReadProgressButton_Click(object sender, RoutedEventArgs e)
	{
		this.ExecuteHandled(() =>
		{
			this.Logger.Log(LogLevel.Information, "Loading game progress", OperationEventId, this);
			Save saveHelper = this.SaveHelper!;

			byte[] rawData = File.ReadAllBytes("./Saves/Native/Decrypted/gameProgress"); // note raw data is zip
			ByteReader reader = new(rawData);
			this.Logger.Log(LogLevel.Information, reader.ReadGameProgress().ToJson(), OperationEventId, this);
		}, "Error while reading:");
	}

	private void EmulateReadSettingsButton_Click(object sender, RoutedEventArgs e)
	{
		this.ExecuteHandled(() =>
		{
			this.Logger.Log(LogLevel.Information, "Loading game settings", OperationEventId, this);
			Save saveHelper = this.SaveHelper!;

			byte[] rawData = File.ReadAllBytes("./Saves/Native/Decrypted/settings"); // note raw data is zip
			ByteReader reader = new(rawData);
			this.Logger.Log(LogLevel.Information, reader.ReadGameSettings().ToJson(), OperationEventId, this);
		}, "Error while reading:");
	}
	private async void EmulateGetRawInfoButton_Click(object sender, RoutedEventArgs e)
	{
		await this.ExecuteHandled(async () =>
		{
			PhigrosLibraryCSharp.Cloud.DataStructure.Raw.RawSaveContainer data = await this.SaveHelper!.GetRawSaveFromCloudAsync();
			this.Logger.Log(LogLevel.Information, data.ToJson(), OperationEventId, this);
		}, "Error while reading:");
	}
	private void DoSomethingButton_Click(object sender, RoutedEventArgs e)
	{
		// put ur code here
	}
	private void FixWizardButton_Click(object sender, RoutedEventArgs e)
	{
		new FixMySaveWindow(this).Show();
	}
}
