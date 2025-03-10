#region

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using HearthMirror;
using Hearthstone_Deck_Tracker.Controls.Stats;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.HsReplay;
using Hearthstone_Deck_Tracker.LogReader;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility;
using Hearthstone_Deck_Tracker.Utility.Analytics;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using Hearthstone_Deck_Tracker.Utility.HotKeys;
using Hearthstone_Deck_Tracker.Utility.LogConfig;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Hearthstone_Deck_Tracker.Windows;
using MahApps.Metro.Controls.Dialogs;
using Hearthstone_Deck_Tracker.Utility.Themes;
using Hearthstone_Deck_Tracker.Utility.Updating;
using WPFLocalizeExtension.Engine;
using Hearthstone_Deck_Tracker.Utility.Assets;
using Hearthstone_Deck_Tracker.Utility.RemoteData;
using System.Linq;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

#endregion

namespace Hearthstone_Deck_Tracker
{
	public static class Core
	{
		internal const int UpdateDelay = 16;
		private static TrayIcon? _trayIcon;
		private static OverlayWindow? _overlay;
		private static Overview? _statsOverview;
		private static int _updateRequestsPlayer;
		private static int _updateRequestsOpponent;
		private static DateTime _startUpTime;
		private static readonly LogWatcherManager LogWatcherManger = new LogWatcherManager();
		public static Version? Version { get; set; }

		internal static GameV2? _game;
		public static GameV2 Game => _game ??= new GameV2();
		private static MainWindow? _mainWindow;
		public static MainWindow MainWindow => _mainWindow ??= new MainWindow();

		public static Overview StatsOverview => _statsOverview ??= new Overview();

		public static bool Initialized { get; private set; }

		public static TrayIcon TrayIcon => _trayIcon ??= new TrayIcon();

		public static OverlayWindow Overlay => _overlay ??= new OverlayWindow(Game);

		internal static bool UpdateOverlay { get; set; } = true;
		internal static bool Update { get; set; }
		internal static bool CanShutdown { get; set; }

		internal static event Action<bool>? GameIsRunningChanged;

#pragma warning disable 1998
		public static async void Initialize()
#pragma warning restore 1998
		{
			LocalizeDictionary.Instance.Culture = CultureInfo.GetCultureInfo("en-US");
			_startUpTime = DateTime.UtcNow;
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
			ServicePointManager.DefaultConnectionLimit = 10;
			Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
			Config.Load();
			Log.Info($"HDT: {Helper.GetCurrentVersion()}, Operating System: {Helper.GetWindowsVersion()}, .NET Framework: {Helper.GetInstalledDotNetVersion()}");
			var splashScreenWindow = new SplashScreenWindow();
#if(SQUIRREL)
			if(Config.Instance.CheckForUpdates)
			{
				var updateCheck = Updater.StartupUpdateCheck(splashScreenWindow);
				while(!updateCheck.IsCompleted)
				{
					await Task.Delay(500);
					if(splashScreenWindow.SkipUpdate)
						break;
				}
			}
#endif
			splashScreenWindow.ShowConditional();
			Log.Initialize();
			Reflection.Exception += e => Log.Warn("HearthMirror Exception: " + e);
			ConfigManager.Run();
			LocUtil.UpdateCultureInfo();
			var newUser = ConfigManager.PreviousVersion == null;
			LogConfigUpdater.Run().Forget();
			LogConfigWatcher.Start();
			UITheme.InitializeTheme().Forget();
			ThemeManager.Run();
			ResourceMonitor.Run();
			Game.SecretsManager.OnSecretsChanged += cards => Overlay.ShowSecrets(cards);
			MainWindow.LoadConfigSettings();
			MainWindow.Show();
			splashScreenWindow.Close();

			if(Config.Instance.GoogleAnalytics)
				HSReplayNetClientAnalytics.Initialize();

			if(Config.Instance.DisplayHsReplayNoteLive && ConfigManager.PreviousVersion != null && ConfigManager.PreviousVersion < new Version(1, 1, 0))
				MainWindow.FlyoutHsReplayNote.IsOpen = true;

			if(ConfigManager.UpdatedVersion != null)
			{
#if(!SQUIRREL)
				Updater.Cleanup();
#endif
				MainWindow.FlyoutUpdateNotes.IsOpen = true;
				MainWindow.UpdateNotesControl.SetHighlight(ConfigManager.PreviousVersion);
#if(SQUIRREL && !DEV)
				if(Config.Instance.CheckForDevUpdates && !Config.Instance.AllowDevUpdates.HasValue)
					MainWindow.ShowDevUpdatesMessage();
#endif
				CheckForHearthstoneUpdate();
			}
			DataIssueResolver.Run();

#if(!SQUIRREL)
			Helper.CopyReplayFiles();
#endif
			BackupManager.Run();

			if(Config.Instance.PlayerWindowOnStart)
				Windows.PlayerWindow.Show();
			if(Config.Instance.OpponentWindowOnStart)
				Windows.OpponentWindow.Show();
			if(Config.Instance.TimerWindowOnStartup)
				Windows.TimerWindow.Show();

			PluginManager.Instance.LoadPluginsFromDefaultPath();
			MainWindow.Options.OptionsTrackerPlugins.Load();
			PluginManager.Instance.StartUpdateAsync();

			UpdateOverlayAsync();

			if(Config.Instance.ShowCapturableOverlay)
			{
				Windows.CapturableOverlay = new CapturableOverlayWindow();
				Windows.CapturableOverlay.Show();
			}

			if(LogConfigUpdater.LogConfigUpdateFailed)
				MainWindow.ShowLogConfigUpdateFailedMessage().Forget();
			else if(LogConfigUpdater.LogConfigUpdated && Game.IsRunning)
			{
				ShowRestartRequiredMessageAsync().Forget();
				Overlay.ShowRestartRequiredWarning();
			}
			LogWatcherManger.Start(Game).Forget();

			Remote.Config.Loaded += CheckForCardImageUpdate;
			Remote.Config.Load();
			HotKeyManager.Load();

			if(Helper.HearthstoneDirExists && Config.Instance.StartHearthstoneWithHDT && !Game.IsRunning)
				HearthstoneRunner.StartHearthstone().Forget();

			HSReplayNetHelper.UpdateAccount().Forget();

			AssetDownloaders.SetupAssetDownloaders();

			if(Config.Instance.BattlegroundsSessionRecapWindowOnStart)
				Windows.BattlegroundsSessionWindow.Show();

			Initialized = true;

			Influx.OnAppStart(
				Helper.GetCurrentVersion(),
				newUser,
				HSReplayNetOAuth.IsFullyAuthenticated,
				HSReplayNetOAuth.AccountData?.IsPremium ?? false,
				(int)(DateTime.UtcNow - _startUpTime).TotalSeconds,
				PluginManager.Instance.Plugins.Count
			);
		}

		private static void CheckForHearthstoneUpdate()
		{
			try
			{
				var hearthDbVersion = HearthDb.Info.HearthDbVersion.ToString();
				if(hearthDbVersion != Config.Instance.HearthdbVersion)
				{
					AssetDownloaders.SetupAssetDownloaders();
					AssetDownloaders.cardImageDownloader?.ClearStorage();
					Config.Instance.HearthdbVersion = hearthDbVersion;
					Config.Save();
				}
			}
			catch(Exception ex)
			{
				Log.Error($"Could not check for updated HearthDB version: {ex}");
			}
		}

		private static void CheckForCardImageUpdate(RemoteData.Config? rConfig)
		{
			try
			{
				var remoteVersion = rConfig?.UpdateInfo?.Version;
				if(remoteVersion.HasValue && remoteVersion > Config.Instance.RemoteHearthstoneVersion)
				{
					AssetDownloaders.SetupAssetDownloaders();
					AssetDownloaders.cardImageDownloader?.ClearStorage();
					Config.Instance.RemoteHearthstoneVersion = remoteVersion.Value;
					Config.Save();
				}
			}
			catch(Exception ex)
			{
				Log.Error($"Could not check for updated remote Hearthstone version: {ex}");
			}
		}

		private static async Task ShowRestartRequiredMessageAsync()
		{
			MainWindow.ActivateWindow();
			while(MainWindow.Visibility != Visibility.Visible || MainWindow.WindowState == WindowState.Minimized)
				await Task.Delay(100);
			await MainWindow.ShowMessageAsync("Hearthstone restart required", "The log.config file has been updated. HDT may not work properly until Hearthstone has been restarted.");
		}

		private static async void UpdateOverlayAsync()
		{
#if(!SQUIRREL)
			if(Config.Instance.CheckForUpdates)
				Updater.CheckForUpdates(true);
#endif
			var hsForegroundChanged = false;
			while(UpdateOverlay)
			{
				if(Config.Instance.CheckForUpdates)
					Updater.CheckForUpdates();
				if(User32.GetHearthstoneWindow() != IntPtr.Zero)
				{
					if(Game.CurrentRegion == Region.UNKNOWN)
					{
						//game started
						Helper.VerifyHearthstonePath();
						Game.CurrentRegion = await Helper.GetCurrentRegion();
						if(Game.CurrentRegion != Region.UNKNOWN)
						{
							BackupManager.Run();
							Game.MetaData.HearthstoneBuild = null;
						}
						Watchers.ExperienceWatcher.Run();

						Remote.Mercenaries.Load();
					}
					Overlay.UpdatePosition();

					if(!Game.IsRunning)
					{
						Overlay.Update(true);
						Windows.CapturableOverlay?.UpdateContentVisibility();
					}

					TrayIcon.MenuItemStartHearthstone.Visible = false;

					Game.IsRunning = true;
					GameIsRunningChanged?.Invoke(true);

					Helper.GameWindowState = User32.GetHearthstoneWindowState();
					Windows.CapturableOverlay?.Update();
					if(User32.IsHearthstoneInForeground() && Helper.GameWindowState != WindowState.Minimized)
					{
						if(hsForegroundChanged)
						{
							Overlay.Update(true);
							if(Config.Instance.WindowsTopmostIfHsForeground && Config.Instance.WindowsTopmost)
							{
								//if player topmost is set to true before opponent:
								//clicking on the playerwindow and back to hs causes the playerwindow to be behind hs.
								//other way around it works for both windows... what?
								Windows.OpponentWindow.Topmost = true;
								Windows.PlayerWindow.Topmost = true;
								Windows.TimerWindow.Topmost = true;
							}
							hsForegroundChanged = false;
						}
					}
					else if(!hsForegroundChanged)
					{
						if(Config.Instance.WindowsTopmostIfHsForeground && Config.Instance.WindowsTopmost)
						{
							Windows.PlayerWindow.Topmost = false;
							Windows.OpponentWindow.Topmost = false;
							Windows.TimerWindow.Topmost = false;
						}
						hsForegroundChanged = true;
					}
				}
				else if(Game.IsRunning)
				{
					Game.IsRunning = false;
					GameIsRunningChanged?.Invoke(false);
					Overlay.ShowOverlay(false);
					Watchers.Stop();
					if(Windows.CapturableOverlay != null)
					{
						Windows.CapturableOverlay.UpdateContentVisibility();
						await Task.Delay(100);
						Windows.CapturableOverlay.ForcedWindowState = WindowState.Minimized;
						Windows.CapturableOverlay.WindowState = WindowState.Minimized;
					}
					Log.Info("Exited game");
					Game.CurrentRegion = Region.UNKNOWN;
					Log.Info("Reset region");
					await Reset();
					Game.IsInMenu = true;
					Game.InvalidateMatchInfoCache();
					Overlay.HideRestartRequiredWarning();
					Helper.ClearCachedHearthstoneBuild();
					TurnTimer.Instance.Stop();

					TrayIcon.MenuItemStartHearthstone.Visible = true;

					if(Config.Instance.CloseWithHearthstone)
						MainWindow.Close();
				}

				await Task.Delay(UpdateDelay);
			}
			CanShutdown = true;
		}

		private static bool _resetting;
		public static async Task Reset()
		{

			if(_resetting)
			{
				Log.Warn("Reset already in progress.");
				return;
			}
			_resetting = true;
			var stoppedReader = await LogWatcherManger.Stop();
			Game.Reset();
			if(DeckList.Instance.ActiveDeck != null)
				Game.IsUsingPremade = true;
			await Task.Delay(1000);
			if(stoppedReader)
				LogWatcherManger.Start(Game).Forget();
			Overlay.HideSecrets();
			Overlay.Update(false);
			UpdatePlayerCards(true);
			_resetting = false;
		}

		internal static async void UpdatePlayerCards(bool reset = false)
		{
			_updateRequestsPlayer++;
			await Task.Delay(100);
			_updateRequestsPlayer--;
			if(_updateRequestsPlayer > 0)
				return;
			var dredged = Game.Player.Deck.Where(x => x.Info.DeckIndex != 0).OrderByDescending(x => x.Info.DeckIndex);
			Card toCard (Entity entity)
			{
				var card = (Card)entity.Card.Clone();
				card.DeckListIndex = entity.Info.DeckIndex;
				return card;
			}
			var top = dredged.Where(x => x.Info.DeckIndex > 0).Select(toCard).ToList();
			var bottom = dredged.Where(x => x.Info.DeckIndex < 0).Select(toCard).ToList();
			Overlay.UpdatePlayerCards(new List<Card>(Game.Player.PlayerCardList), reset, top, bottom);
			if(Windows.PlayerWindow.IsVisible)
				Windows.PlayerWindow.UpdatePlayerCards(new List<Card>(Game.Player.PlayerCardList), reset, top, bottom);
		}

		internal static async void UpdateOpponentCards(bool reset = false)
		{
			_updateRequestsOpponent++;
			await Task.Delay(100);
			_updateRequestsOpponent--;
			if(_updateRequestsOpponent > 0)
				return;
			Overlay.UpdateOpponentCards(new List<Card>(Game.Opponent.OpponentCardList), reset);
			if(Windows.OpponentWindow.IsVisible)
				Windows.OpponentWindow.UpdateOpponentCards(new List<Card>(Game.Opponent.OpponentCardList), reset);
		}

		internal static async Task StopLogWacher() => await LogWatcherManger.Stop(true);

		public static class Windows
		{
			private static PlayerWindow? _playerWindow;
			private static OpponentWindow? _opponentWindow;
			private static BattlegroundsSessionWindow? _bgsSessionWindow;
			private static TimerWindow? _timerWindow;
			private static StatsWindow? _statsWindow;

			public static PlayerWindow PlayerWindow => _playerWindow ??= new PlayerWindow(Game);
			public static OpponentWindow OpponentWindow => _opponentWindow ??= new OpponentWindow(Game);
			public static BattlegroundsSessionWindow BattlegroundsSessionWindow => _bgsSessionWindow ??= new BattlegroundsSessionWindow();
			public static TimerWindow TimerWindow => _timerWindow ??= new TimerWindow(Config.Instance);
			public static StatsWindow StatsWindow => _statsWindow ??= new StatsWindow();
			public static CapturableOverlayWindow? CapturableOverlay;
		}

		internal static bool StatsOverviewInitialized => _statsOverview != null;
	}
}
