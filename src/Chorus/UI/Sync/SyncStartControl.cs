﻿using System;
using System.Threading;
using System.Windows.Forms;
using Chorus.UI.Misc;
using Chorus.VcsDrivers;
using Chorus.VcsDrivers.Mercurial;
using Palaso.Code;
using Palaso.Progress.LogBox;

namespace Chorus.UI.Sync
{
	internal partial class SyncStartControl : UserControl
	{
		private HgRepository _repository;
		private SyncStartModel _model;
		public event EventHandler<SyncStartArgs> RepositoryChosen;
		private const string _connectionDiagnostics = "There was a problem connecting to the {0}.\r\n{1}Connection attempt failed.";

		private Thread _updateInternetSituation; // Thread that runs the Internet status checking worker.
		private ConnectivityStateWorker _internetStateWorker;
		private bool _internetWorkerStarted = false; // Has worker been started?

		private Thread _updateNetworkSituation; // Thread that runs the Network Folder status checking worker.
		private ConnectivityStateWorker _networkStateWorker;
		private bool _networkWorkerStarted = false; // Has worker been started?

		private bool _exiting; // Dialog is in the process of exiting, stop the threads!

		private const int STATECHECKINTERVAL = 2000; // 2 sec interval between checks of USB status.
		private const int INITIALINTERVAL = 1000; // only wait 1 sec, the first time
		const string SetupLinkText = " Set Up"; // Label for status line Setup link

		private delegate void UpdateInternetUICallback(bool enabled, string btnLabel, string message, string tooltip, string diagnostics);

		private delegate void UpdateNetworkUICallback(bool enabled, string message, string tooltip, string diagnostics);

		//designer only
		public SyncStartControl()
		{
			InitializeComponent();
		}

		public SyncStartControl(HgRepository repository)
		{
			InitializeComponent();
			Init(repository);
		}

		public void Init(HgRepository repository)
		{
			Guard.AgainstNull(repository, "repository");
			SetupSharedFolderAndInternetUI();

			_model = new SyncStartModel(repository);
			_repository = repository;
			_userName.Text = repository.GetUserIdInUse();

			// Setup Internet State Checking thread and the worker that it will run
			_internetStateWorker = new ConnectivityStateWorker(CheckInternetStatusAndUpdateUI);
			_updateInternetSituation = new Thread(_internetStateWorker.DoWork);

			// Setup Shared Network Folder Checking thread and its worker
			_networkStateWorker = new ConnectivityStateWorker(CheckNetworkStatusAndUpdateUI);
			_updateNetworkSituation = new Thread(_networkStateWorker.DoWork);

			// let the dialog display itself first, then check for connection
			_updateDisplayTimer.Interval = INITIALINTERVAL; // But check sooner than 2 seconds anyway!
			_updateDisplayTimer.Enabled = true;
		}

		private void SetupSharedFolderAndInternetUI()
		{
			const string checkingConnection = "Checking connection...";
			_useSharedFolderStatusLabel.Text = checkingConnection;
			_useSharedFolderButton.Enabled = false;

			_internetStatusLabel.Text = checkingConnection;
			_useInternetButton.Enabled = false;
		}

		private void OnUpdateDisplayTick(object sender, EventArgs e)
		{
			_updateDisplayTimer.Interval = STATECHECKINTERVAL; // more normal checking rate from here on out
			UpdateDisplay();
		}

		private void UpdateDisplay()
		{
			UpdateUsbDriveSituation();
			UpdateInternetSituation();
			UpdateLocalNetworkSituation();
			UpdateSetupButtonUI();
		}

		private void UpdateSetupButtonUI()
		{
			const int LengthOfSetUpString = 7;
			if (ShouldShowNetworkSetUpButton)
			{
				// Make sure "Set Up" button is active
				if (!NetworkSetupButtonIsActive)
					_useSharedFolderStatusLabel.Text += SetupLinkText;
			}
			else
			{
				if (NetworkSetupButtonIsActive)
				{
					// Make sure "Set Up" button is inactive
					var oldLabel = _useSharedFolderStatusLabel.Text;
					_useSharedFolderStatusLabel.Text = oldLabel.Substring(0, oldLabel.Length - LengthOfSetUpString);
				}
			}

			if (ShouldShowInternetSetUpButton)
			{
				// Make sure "Set Up" button is active
				if (!InternetSetupButtonIsActive)
					_internetStatusLabel.Text += SetupLinkText;
			}
			else
			{
				if (InternetSetupButtonIsActive)
				{
					// Make sure "Set Up" button is inactive
					var oldLabel = _internetStatusLabel.Text;
					_internetStatusLabel.Text = oldLabel.Substring(0, oldLabel.Length - LengthOfSetUpString);
				}
			}
		}

		#region Network Status methods

		public bool ShouldShowNetworkSetUpButton
		{
			get { return (!_useSharedFolderButton.Enabled || Control.ModifierKeys == Keys.Shift); }
		}

		protected bool NetworkSetupButtonIsActive
		{
			get { return _useSharedFolderStatusLabel.Text.EndsWith(SetupLinkText); }
		}

		private void UpdateLocalNetworkSituation()
		{
			if (!_networkWorkerStarted)
			{
				_networkWorkerStarted = true;
				_updateNetworkSituation.Start();
			}
		}

		/// <summary>
		/// Called by our worker thread to avoid inordinate pauses in the UI while checking the
		/// Shared Network Folder to determine its status.
		/// </summary>
		private void CheckNetworkStatusAndUpdateUI()
		{
			// Check network Shared Folder status
			string message, tooltip, diagnostics;
			Monitor.Enter(_model);
			bool result = _model.GetNetworkStatusLink(out message, out tooltip, out diagnostics);
			Monitor.Exit(_model);

			Monitor.Enter(this);
			// Using a callback and Invoke ensures that we avoid cross-threading updates.
			if (!_exiting)
			{
				var callback = new UpdateNetworkUICallback(UpdateNetworkUI);
				this.Invoke(callback, new object[] {result, message, tooltip, diagnostics});
			}
			Monitor.Exit(this);
		}

		/// <summary>
		/// Callback method to ensure that Controls are painted on the main thread and not the worker thread.
		/// </summary>
		/// <param name="enabled"></param>
		/// <param name="message"></param>
		/// <param name="tooltip"></param>
		/// <param name="diagnostics"></param>
		private void UpdateNetworkUI(bool enabled, string message, string tooltip, string diagnostics)
		{
			_useSharedFolderButton.Enabled = enabled;
			if (!string.IsNullOrEmpty(diagnostics))
				SetupNetworkDiagnosticLink(diagnostics);
			else
				_sharedNetworkDiagnosticsLink.Visible = false;

			_useSharedFolderStatusLabel.Text = message;
			_useSharedFolderStatusLabel.LinkArea = new LinkArea(message.Length + 1, 1000);
			if (_useSharedFolderButton.Enabled)
				tooltip += System.Environment.NewLine + "Press Shift to see Set Up button";
			toolTip1.SetToolTip(_useSharedFolderButton, tooltip);
		}

		private void SetupNetworkDiagnosticLink(string diagnosticText)
		{
			_sharedNetworkDiagnosticsLink.Tag = diagnosticText;
			_sharedNetworkDiagnosticsLink.Enabled = _sharedNetworkDiagnosticsLink.Visible = true;
		}

		#endregion // Network

		#region Internet Status methods

		public bool ShouldShowInternetSetUpButton
		{
			get { return (!_useInternetButton.Enabled || Control.ModifierKeys == Keys.Shift); }
		}

		protected bool InternetSetupButtonIsActive
		{
			get { return _internetStatusLabel.Text.EndsWith(SetupLinkText); }
		}

		/// <summary>
		/// Pings to test Internet connectivity were causing several second pauses in the dialog.
		/// Now the Internet situation is determined in a separate worker thread which reports
		/// back to the main one.
		/// </summary>
		private void UpdateInternetSituation()
		{
			if (!_internetWorkerStarted)
			{
				_internetWorkerStarted = true;
				_updateInternetSituation.Start();
			}
		}

		/// <summary>
		/// Called by our worker thread to avoid inordinate pauses in the UI while the Internet
		/// is pinged to determine its status.
		/// </summary>
		private void CheckInternetStatusAndUpdateUI()
		{
			// Check Internet status
			string buttonLabel, message, tooltip, diagnostics;
			Monitor.Enter(_model);
			bool result = _model.GetInternetStatusLink(out buttonLabel, out message, out tooltip,
													   out diagnostics);
			Monitor.Exit(_model);

			// Using a callback and Invoke ensures that we avoid cross-threading updates.
			var callback = new UpdateInternetUICallback(UpdateInternetUI);
			Monitor.Enter(this);
			if(!_exiting)
				this.Invoke(callback, new object[] { result, buttonLabel, message, tooltip, diagnostics });
			Monitor.Exit(this);
		}

		/// <summary>
		/// Callback method to ensure that Controls are painted on the main thread and not the worker thread.
		/// </summary>
		/// <param name="enabled"></param>
		/// <param name="btnLabel"></param>
		/// <param name="message"></param>
		/// <param name="tooltip"></param>
		/// <param name="diagnostics"></param>
		private void UpdateInternetUI(bool enabled, string btnLabel, string message, string tooltip, string diagnostics)
		{
			_useInternetButton.Enabled = enabled;
			if (!string.IsNullOrEmpty(diagnostics))
				SetupInternetDiagnosticLink(diagnostics);
			else
				_internetDiagnosticsLink.Visible = false;

			_useInternetButton.Text = btnLabel;
			_internetStatusLabel.Text = message;
			_internetStatusLabel.LinkArea = new LinkArea(message.Length + 1, 1000);
			if (_useInternetButton.Enabled)
				tooltip += System.Environment.NewLine + "Press Shift to see Set Up button";
			toolTip1.SetToolTip(_useInternetButton, tooltip);
		}

		private void SetupInternetDiagnosticLink(string diagnosticText)
		{
			_internetDiagnosticsLink.Tag = diagnosticText;
			_internetDiagnosticsLink.Enabled = _internetDiagnosticsLink.Visible = true;
		}

		#endregion  // Internet

		private void UpdateUsbDriveSituation()
		{
			// usbDriveLocator is defined in the Designer
			string message;
			_useUSBButton.Enabled = _model.GetUsbStatusLink(usbDriveLocator, out message);
			_usbStatusLabel.Text = message;
		}

		private void _useUSBButton_Click(object sender, EventArgs e)
		{
			if (RepositoryChosen != null)
			{
				UpdateName();
				var address = RepositoryAddress.Create(RepositoryAddress.HardWiredSources.UsbKey, "USB flash drive", false);
				RepositoryChosen.Invoke(this, new SyncStartArgs(address, _commitMessageText.Text));
			}
		}

		private void UpdateName()
		{
			if (_repository.GetUserIdInUse() != _userName.Text.Trim() && _userName.Text.Trim().Length>0)
			{
				_repository.SetUserNameInIni(_userName.Text.Trim(), new NullProgress());
			}
		}

		private void _useInternetButton_Click(object sender, EventArgs e)
		{
			if (RepositoryChosen != null)
			{
				UpdateName();
				var address = _repository.GetDefaultNetworkAddress<HttpRepositoryPath>();
				RepositoryChosen.Invoke(this, new SyncStartArgs(address, _commitMessageText.Text));
			}
		}

		private void _useSharedFolderButton_Click(object sender, EventArgs e)
		{
			if (RepositoryChosen != null)
			{
				UpdateName();
				var address = _repository.GetDefaultNetworkAddress<DirectoryRepositorySource>();
				RepositoryChosen.Invoke(this, new SyncStartArgs(address, _commitMessageText.Text));
			}
		}

		private void _internetStatusLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			DialogResult dlgResult;
			using (var dlg = new ServerSettingsDialog(_repository.PathToRepo))
			{
				dlgResult = dlg.ShowDialog();
			}
			if (dlgResult == DialogResult.OK)
				RecheckInternetStatus();
		}

		private void RecheckInternetStatus()
		{
			_internetWorkerStarted = false;
			// Setup Internet State Checking thread and the worker that it will run
			_internetStateWorker = new ConnectivityStateWorker(CheckInternetStatusAndUpdateUI);
			_updateInternetSituation = new Thread(_internetStateWorker.DoWork);
		}

		private void _sharedFolderStatusLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			if(DialogResult.Cancel ==
				MessageBox.Show(
				"Sharing repositories over a local network may sometimes cause a repository to become corrupted. This can be repaired by copying one of the good copies of the repository, but it may require expert help. If you have a good internet connection or a small enough group to pass a USB key around, we recommend one of the other Send/Receive options.",
				"Warning", MessageBoxButtons.OKCancel))
			{
				return;
			}
			DialogResult dlgResult;
			using (var dlg =  new System.Windows.Forms.FolderBrowserDialog())
			{
				dlg.ShowNewFolderButton = true;
				dlg.Description = "Choose the folder containing the project with which you want to synchronize.";

				while (true)
				{
					dlgResult = dlg.ShowDialog();
					if (dlgResult != DialogResult.OK)
						return;
					Monitor.Enter(_model);
					var networkedDriveOK = _model.SetNewSharedNetworkAddress(_repository, dlg.SelectedPath);
					Monitor.Exit(_model);
					if (networkedDriveOK)
						break;
				}
			}
			if (dlgResult == DialogResult.OK)
				RecheckNetworkStatus();
		}

		private void RecheckNetworkStatus()
		{
			_networkWorkerStarted = false;
			// Setup Shared Network Folder Checking thread and its worker
			_networkStateWorker = new ConnectivityStateWorker(CheckNetworkStatusAndUpdateUI);
			_updateNetworkSituation = new Thread(_networkStateWorker.DoWork);
		}

		private void _internetDiagnosticsLink_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			Palaso.Reporting.ErrorReport.NotifyUserOfProblem(_connectionDiagnostics,
				"Internet", (string)_internetDiagnosticsLink.Tag);
		}

		private void _sharedNetworkDiagnosticsLink_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
		{
			Palaso.Reporting.ErrorReport.NotifyUserOfProblem(_connectionDiagnostics,
				"Shared Network Folder", (string)_sharedNetworkDiagnosticsLink.Tag);
		}

		/// <summary>
		/// Class to run a separate worker thread to check connectivity status.
		/// </summary>
		internal class ConnectivityStateWorker
		{
			private Action _action;

			internal ConnectivityStateWorker(Action action)
			{
				_action = action;
			}

			internal void DoWork()
			{
				_action();
			}
		}
	}

	public class SyncStartArgs : EventArgs
	{
		public SyncStartArgs(RepositoryAddress address, string commitMessage)
		{
			Address = address;
			CommitMessage = commitMessage;
		}
		public RepositoryAddress Address;
		public string CommitMessage;
	}
}
