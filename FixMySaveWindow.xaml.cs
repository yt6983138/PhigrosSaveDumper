using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using PhigrosLibraryCSharp.CloudSave;
using PhigrosLibraryCSharp.CloudSave.HttpModels;
using System.Windows;

namespace PhigrosSaveDumper;
/// <summary>
/// Interaction logic for FixMySaveWindow.xaml
/// </summary>
public partial class FixMySaveWindow : Window
{
	private MainWindow _mainWindow;

	public FixMySaveWindow(MainWindow main)
	{
		this.InitializeComponent();
		this._mainWindow = main;
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		this.Close();
	}
	private async void FixNowButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			int sourceIndex = this.SourceIndex.Value.EnsureNotNull();
			int targetIndex = this.TargetIndex.Value.EnsureNotNull();

			if (sourceIndex == targetIndex &&
				MessageBox.Show(
					"Warning: The target and source index is the same, do you wish to proceed?",
					"Save Fix Wizard",
					MessageBoxButton.YesNo) == MessageBoxResult.No)
			{
				return;
			}

			this._mainWindow.Logger.LogInformation("Fixing save at index {index}...", sourceIndex);

			Save save = this._mainWindow.SaveOrThrow;
			JObject rawSave = await SaveExpansion.FetchRawSaveAsNode(save);
			this._mainWindow.Logger.LogInformation("Raw save: {save}", rawSave.ToJson());
			JToken results = rawSave["results"]!;
			JToken? toFixValue = this.TargetIndex.IsEnabled ? results[targetIndex]! : null;

			JToken sourceValue = results[sourceIndex]!;

			byte[] raw = await save.GetSaveZipAsync(new PhiCloudObj() { Url = (string)sourceValue["gameFile"]!["url"]! });

			this._mainWindow.Logger.LogInformation("Uploading...");
			await SaveExpansion.UploadSave(
				save,
				(string)sourceValue["user"]!["objectId"]!,
				null,
				(string?)toFixValue?["objectId"],
				raw,
				Convert.FromBase64String((string)sourceValue["summary"]!));
			this._mainWindow.Logger.LogInformation("Uploading complete");
			MessageBox.Show("Fixed successfully.", "Fix Wizard Result", MessageBoxButton.OK);
			this.Close();
		}
		catch (Exception ex)
		{
			this._mainWindow.Logger.LogError(ex, "Error while fixing save:");
			MessageBox.Show($"An error occurred while fixing the save: {ex.Message}", "Fix Wizard Error", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}
	private async void ShowIndexesButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Save save = this._mainWindow.SaveOrThrow;
			JObject rawSave = await SaveExpansion.FetchRawSaveAsNode(save);
			this._mainWindow.Logger.LogInformation("Raw save: {save}", rawSave.ToJson());
			JToken results = rawSave["results"]!;

			string message = string.Join("\n",
				results.Select((x, i) => $"{i}: created at {x["createdAt"]}, modified at {x["modifiedAt"]?["iso"]}"));
			MessageBox.Show(message, "Save Indexes", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex)
		{
			this._mainWindow.Logger.LogError(ex, "Error while fixing save:");
			MessageBox.Show($"An error occurred while fixing the save: {ex.Message}", "Fix Wizard Error", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}
	private void CheckBox_Clicked(object sender, RoutedEventArgs e)
	{
		this.TargetIndex.IsEnabled = !this.TargetIndex.IsEnabled;
	}
}
