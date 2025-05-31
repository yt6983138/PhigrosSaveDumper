using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using PhigrosLibraryCSharp;
using PhigrosLibraryCSharp.Cloud.DataStructure.Raw;
using System.Windows;
using yt6983138.Common;

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

			this._mainWindow.Logger.Log(LogLevel.Information, $"Fixing save at index {sourceIndex}...", MainWindow.OperationEventId, this);

			Save save = this._mainWindow.SaveHelper!;
			JObject rawSave = await SaveUploader.FetchRawSaveAsNode(save);
			this._mainWindow.Logger.Log(LogLevel.Information, $"Raw save: {rawSave.ToJson()}", MainWindow.OperationEventId, this);
			JToken results = rawSave["results"]!;
			JToken toFixValue = results[targetIndex]!;

			JToken sourceValue = results[sourceIndex]!;

			byte[] raw = await save.GetSaveRawZipAsync(new PhiCloudObj() { Url = (string)sourceValue["gameFile"]!["url"]! });

			this._mainWindow.Logger.Log(LogLevel.Information, $"Uploading...", MainWindow.OperationEventId, this);
			await SaveUploader.UploadSave(
				save,
				(string)toFixValue["user"]!["objectId"]!,
				null,
				(string)toFixValue["objectId"]!,
				raw,
				Convert.FromBase64String((string)sourceValue["summary"]!));
			this._mainWindow.Logger.Log(LogLevel.Information, $"Upload complete.", MainWindow.OperationEventId, this);
			MessageBox.Show("Fixed successfully.", "Fix Wizard Result", MessageBoxButton.OK);
			this.Close();
		}
		catch (Exception ex)
		{
			this._mainWindow.Logger.Log(LogLevel.Error, $"Error while fixing save: {ex.Message}", MainWindow.OperationEventId, this, ex);
			MessageBox.Show($"An error occurred while fixing the save: {ex.Message}", "Fix Wizard Error", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}
	private async void ShowIndexesButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Save save = this._mainWindow.SaveHelper!;
			JObject rawSave = await SaveUploader.FetchRawSaveAsNode(save);
			this._mainWindow.Logger.Log(LogLevel.Information, $"Raw save: {rawSave.ToJson()}", MainWindow.OperationEventId, this);
			JToken results = rawSave["results"]!;

			string message = string.Join("\n",
				results.Select((x, i) => $"{i}: created at {x["createdAt"]}, modified at {x["modifiedAt"]?["iso"]}"));
			MessageBox.Show(message, "Save Indexes", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex)
		{
			this._mainWindow.Logger.Log(LogLevel.Error, $"Error while fixing save: {ex.Message}", MainWindow.OperationEventId, this, ex);
			MessageBox.Show($"An error occurred while fixing the save: {ex.Message}", "Fix Wizard Error", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}
}
