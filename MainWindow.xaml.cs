using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using PhigrosLibraryCSharp.CloudSave;
using PhigrosLibraryCSharp.CloudSave.Login;
using PhigrosLibraryCSharp.Serialization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace PhigrosSaveDumper;
/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
	internal ILogger Logger { get; set; }

	public Save? SaveHelper
	{
		get;
		set
		{
			field = value;
			field?.RequestHandler = (save, request) => this.CommonRequestHandler(save.Client, request);

			OnLockOrUnlock?.Invoke(this, value);
		}
	}
	internal event EventHandler<Save?>? OnLockOrUnlock;

	public Save SaveOrThrow => this.SaveHelper
		?? throw new InvalidOperationException("Save is not locked yet.");

	public MainWindow()
	{
		this.InitializeComponent();
		OnLockOrUnlock += (_, obj) => this.OperationGroupBox.IsEnabled = obj is not null;
		OnLockOrUnlock += (_, obj) => this.SettingsGroupBox.IsEnabled = obj is not null;
		this.SaveHelper = null;

		LogManager.Setup().SetupExtensions(ext =>
		{
			ext.RegisterTarget<EventLogTarget>();
		}).LoadConfigurationFromFile();

		EventLogTarget target = LogManager.Configuration?.FindTargetByName<EventLogTarget>("eventLog")
			?? throw new InvalidOperationException("Cannot find event log target");

		this.Logger = new NLogLoggerProvider().CreateLogger(nameof(MainWindow));

		target.LogEmitted += (sender, logEvent) =>
		{
			if (sender.AllLogs.Count == 1)
				this.LogOutput.Text = "";

			this.LogOutput.Text += $"{logEvent.Rendered}\n";
			this.LogOutput.ScrollToEnd();
		};

		TapTapHelper.Proxy = this.CommonRequestHandler;

		if (!Directory.Exists("./Saves/"))
			Directory.CreateDirectory("./Saves/");
	}

	private async Task<HttpResponseMessage> CommonRequestHandler(HttpClient client, HttpRequestMessage request)
	{
		int hash = request.GetHashCode();
		this.Logger.LogDebug("Requesting: ({hash}) {request}", hash, request);
		HttpResponseMessage response = await client.SendAsync(request);
		await response.Content.LoadIntoBufferAsync();
		Stream stream = await response.Content.ReadAsStreamAsync();

		string? mediaType = response.Content.Headers.ContentType?.MediaType;

		if ((mediaType?.Contains("application/json", StringComparison.OrdinalIgnoreCase)).GetValueOrDefault())
		{
			string content = await response.Content.ReadAsStringAsync();
			try
			{
				content = JsonNode.Parse(content)?.ToJsonString(Extension._jsonOptions) ?? content;
			}
			catch { }

			this.Logger.LogDebug("Response: ({hash}) {stat}: {message}", hash, response.StatusCode, content);
		}
		else
		{
			this.Logger.LogDebug("Response: ({hash}) {stat}: <not json: {type}>", hash, response.StatusCode, mediaType);
		}

		stream.Seek(0, SeekOrigin.Begin);
		return response;
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
			this.Logger.LogError(ex, errorMessage);
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
			this.Logger.LogError(ex, errorMessage);
			return false;
		}
	}

	private async void LoginTapTap(object _, RoutedEventArgs _2)
	{
		this.Logger.LogInformation("TapTap login started...");
		await this.ExecuteHandled(async () =>
		{
			this.Logger.LogInformation("Requesting login link...");
			CompleteQRCodeData qrCode = await TapTapHelper.RequestLoginQrCode();
			Process.Start(new ProcessStartInfo(qrCode.Url) { UseShellExecute = true });
			this.Logger.LogInformation("QRCode url generated: {url}, expires in {sec} seconds.", qrCode.Url, qrCode.ExpiresInSeconds);
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

					this.Logger.LogInformation("Login result: {result}", result.ToJson());
					this.Logger.LogInformation("Profile result: {profile}", profile.ToJson());

					LCCombinedAuthData completed = new(profile.Data, result.Data);
					JsonNode invoked = await LCHelper.LoginWithAuthData(completed);

					this.Logger.LogInformation("Final result: {result}", invoked.ToJsonString());

					string token = ((string?)invoked["sessionToken"]).EnsureNotNull();
					this.TokenTextBox.Text = token;
					this.Logger.LogInformation("Got token: {token}", token);
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
			this.Logger.LogInformation("Locking token...");
			Save helper = new(this.TokenTextBox.Text.Trim(), this.InternationalMode.IsChecked.GetValueOrDefault());
			_ = await helper.GetPlayerInfoAsync();
			this.SaveHelper = helper;
			this.TokenTextBox.IsEnabled = false;
			this.Logger.LogInformation("Token locked.");
		}, $"Error while locking:"))
		{
			goto Final;
		}

		return;
	Final:
		this.SaveHelper = null;
		this.TokenTextBox.IsEnabled = true;
		this.Logger.LogInformation("Token unlocked.");
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
				this.Logger.LogInformation("Renamed folder from 'Native' to '{date}'.", dateTimeString);
			}, "Error while moving folder:"))
			{
				return;
			}
		}
		await this.ExecuteHandled(async () =>
		{
			await this.SaveOrThrow.UnpackRawZip(this.TimeIndexSelector.Value ?? 0, nativeDir, this.Logger);
		}, "Error while unpacking:");
	}

	private async void ListTimeIndex_Click(object sender, RoutedEventArgs e)
	{
		await this.ExecuteHandled(async () =>
		{
			int i = 0;
			string message = string.Join("\n",
					(await this.SaveOrThrow.GetSaveInfoFromCloudAsync())
					.GetParsedSaves()
					.Select(x => $"{i++}: {x.ModificationTime}")
				);
			MessageBox.Show(message);
			this.Logger.LogInformation("Listing indexes...\n{message}", message);
		}, "Error while listing index:");
	}

	private async void EmulateReadRecordButton_Click(object sender, RoutedEventArgs e)
	{
		await this.ExecuteHandled(async () =>
		{
			ByteReader byteReader = new(await File.ReadAllBytesAsync("./Saves/Native/Decrypted/gameRecord"));

			GameRecord record = GameRecord.FromReader(byteReader);
			Random.Shared.Shuffle(CollectionsMarshal.AsSpan(record.Records));
			this.Logger.LogInformation("Printing random 10...");
			for (int i = 0; i < 10; i++)
			{
				SongScore score = record.Records[i];
				this.Logger.LogInformation("{index}: {id}/{score}/{acc}", i, score.Id, score.Score, score.Accuracy);
			}
		}, "Error while reading:");
	}

	private async void EmulateReadUserInfoButton_Click(object sender, RoutedEventArgs e)
	{
		await this.ExecuteHandled(async () =>
		{
			this.Logger.LogInformation("Loading user info...");

			await this.SaveOrThrow.GetPlayerInfoAsync(); // logged from interceptor
		}, "Error while reading:");
	}

	private void EmulateReadGameUserInfoButton_Click(object sender, RoutedEventArgs e)
	{
		this.ExecuteHandled(() =>
		{
			this.Logger.LogInformation("Loading game user info...");
			ByteReader reader = new(File.ReadAllBytes("./Saves/Native/Decrypted/user"));

			this.Logger.LogInformation("{message}", GameUserInfo.FromReader(reader).ToJson());
		}, "Error while reading:");
	}

	private void EmulateReadProgressButton_Click(object sender, RoutedEventArgs e)
	{
		this.ExecuteHandled(() =>
		{
			this.Logger.LogInformation("Loading game progress...");
			ByteReader reader = new(File.ReadAllBytes("./Saves/Native/Decrypted/gameProgress"));

			this.Logger.LogInformation("{message}", GameProgress.FromReader(reader).ToJson());
		}, "Error while reading:");
	}

	private void EmulateReadSettingsButton_Click(object sender, RoutedEventArgs e)
	{
		this.ExecuteHandled(() =>
		{
			this.Logger.LogInformation("Loading game settings...");
			ByteReader reader = new(File.ReadAllBytes("./Saves/Native/Decrypted/settings"));

			this.Logger.LogInformation("{message}", GameSettings.FromReader(reader).ToJson());
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
