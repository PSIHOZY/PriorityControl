using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using PriorityControl.Models;
using PriorityControl.Services;

namespace PriorityControl.UI
{
    public sealed class MainForm : Form
    {
        private const string PropertyExePath = "ExePath";
        private const string PropertyPriority = "Priority";
        private const string PropertyRunOnStartupWithLock = "RunOnStartupWithLock";
        private const string PropertyRuntimeStatus = "RuntimeStatus";

        private readonly SettingsService _settingsService = new SettingsService();
        private readonly StartupService _startupService = new StartupService();
        private readonly ElevationService _elevationService = new ElevationService();
        private readonly PriorityProcessService _processService = new PriorityProcessService();

        private readonly BindingList<AppEntry> _entries = new BindingList<AppEntry>();
        private readonly BindingSource _entriesSource = new BindingSource();

        private readonly bool _startedFromStartup;
        private readonly string[] _launchArgs;

        private bool _isInitializing;
        private bool _isGridEditing;

        private DataGridView _grid;
        private Button _addButton;
        private Button _removeButton;
        private Button _startButton;
        private Button _lockButton;
        private Button _unlockButton;
        private Button _refreshButton;
        private Button _restartAsAdminButton;
        private CheckBox _startWithWindowsCheckBox;
        private Label _adminModeLabel;
        private Label _infoLabel;
        private Timer _statusTimer;

        public MainForm(bool startedFromStartup, string[] launchArgs)
        {
            _startedFromStartup = startedFromStartup;
            _launchArgs = launchArgs ?? new string[0];

            InitializeComponent();
            LoadFromSettings();
            UpdateAdminState();
            RefreshAllStatuses();
            UpdateButtonsState();

            _statusTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _statusTimer.Stop();
            SaveSettings();
            _processService.Dispose();
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (_startedFromStartup)
            {
                StartConfiguredStartupEntries();
            }
        }

        private void InitializeComponent()
        {
            Text = "PriorityControl";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 520);
            Size = new Size(1180, 640);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(layout);

            var topPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            layout.Controls.Add(topPanel, 0, 0);

            _adminModeLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 11, 15, 0)
            };
            topPanel.Controls.Add(_adminModeLabel);

            _restartAsAdminButton = new Button
            {
                Text = "Restart as administrator",
                AutoSize = true,
                Margin = new Padding(0, 6, 15, 0)
            };
            _restartAsAdminButton.Click += RestartAsAdminButton_Click;
            topPanel.Controls.Add(_restartAsAdminButton);

            _startWithWindowsCheckBox = new CheckBox
            {
                AutoSize = true,
                Text = "Start PriorityControl with Windows",
                Margin = new Padding(0, 10, 0, 0)
            };
            _startWithWindowsCheckBox.CheckedChanged += StartWithWindowsCheckBox_CheckedChanged;
            topPanel.Controls.Add(_startWithWindowsCheckBox);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EditMode = DataGridViewEditMode.EditOnEnter,
                AutoGenerateColumns = false,
                RowHeadersVisible = false
            };
            _grid.CellValueChanged += Grid_CellValueChanged;
            _grid.CurrentCellDirtyStateChanged += Grid_CurrentCellDirtyStateChanged;
            _grid.CellBeginEdit += Grid_CellBeginEdit;
            _grid.CellEndEdit += Grid_CellEndEdit;
            _grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs args) { args.ThrowException = false; };
            _grid.SelectionChanged += delegate { UpdateButtonsState(); };
            layout.Controls.Add(_grid, 0, 1);

            var pathColumn = new DataGridViewTextBoxColumn
            {
                HeaderText = "Executable Path",
                DataPropertyName = PropertyExePath,
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 48
            };

            var priorityColumn = new DataGridViewComboBoxColumn
            {
                HeaderText = "Fixed Priority",
                DataPropertyName = PropertyPriority,
                DataSource = Enum.GetValues(typeof(FixedPriority)),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
                FlatStyle = FlatStyle.Flat
            };

            var startupColumn = new DataGridViewCheckBoxColumn
            {
                HeaderText = "Run on startup with lock",
                DataPropertyName = PropertyRunOnStartupWithLock,
                Width = 180
            };

            var statusColumn = new DataGridViewTextBoxColumn
            {
                HeaderText = "Status",
                DataPropertyName = PropertyRuntimeStatus,
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 52
            };

            _grid.Columns.Add(pathColumn);
            _grid.Columns.Add(priorityColumn);
            _grid.Columns.Add(startupColumn);
            _grid.Columns.Add(statusColumn);

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            layout.Controls.Add(buttonPanel, 0, 2);

            _addButton = new Button { Text = "Add...", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
            _removeButton = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
            _startButton = new Button { Text = "Start with fixed priority", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
            _lockButton = new Button { Text = "Apply priority lock", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
            _unlockButton = new Button { Text = "Stop priority lock", AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
            _refreshButton = new Button { Text = "Refresh status", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

            _addButton.Click += AddButton_Click;
            _removeButton.Click += RemoveButton_Click;
            _startButton.Click += StartButton_Click;
            _lockButton.Click += LockButton_Click;
            _unlockButton.Click += UnlockButton_Click;
            _refreshButton.Click += delegate { RefreshAllStatuses(); };

            buttonPanel.Controls.Add(_addButton);
            buttonPanel.Controls.Add(_removeButton);
            buttonPanel.Controls.Add(_startButton);
            buttonPanel.Controls.Add(_lockButton);
            buttonPanel.Controls.Add(_unlockButton);
            buttonPanel.Controls.Add(_refreshButton);

            _infoLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            layout.Controls.Add(_infoLabel, 0, 3);

            _entriesSource.DataSource = _entries;
            _grid.DataSource = _entriesSource;

            _statusTimer = new Timer { Interval = 2000 };
            _statusTimer.Tick += delegate
            {
                if (!_isGridEditing)
                {
                    RefreshAllStatuses();
                }
            };
        }

        private void LoadFromSettings()
        {
            _isInitializing = true;
            AppSettings settings = _settingsService.Load();

            _entries.RaiseListChangedEvents = false;
            _entries.Clear();
            foreach (AppEntry entry in settings.Entries)
            {
                entry.RuntimeStatus = "Not running";
                entry.ProcessId = null;
                entry.IsPriorityLocked = false;
                _entries.Add(entry);
            }

            _entries.RaiseListChangedEvents = true;
            _entriesSource.ResetBindings(false);

            bool runValueEnabled = _startupService.IsEnabled();
            _startWithWindowsCheckBox.Checked = settings.StartWithWindows && runValueEnabled;
            _isInitializing = false;

            ClearGridSelection();
        }

        private void SaveSettings()
        {
            var settings = new AppSettings
            {
                Entries = _entries.ToList(),
                StartWithWindows = _startWithWindowsCheckBox.Checked
            };
            _settingsService.Save(settings);
        }

        private void AddButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select executable files";
                dialog.Filter = "Executable files (*.exe)|*.exe";
                dialog.Multiselect = true;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                int added = 0;
                foreach (string path in dialog.FileNames)
                {
                    bool exists = _entries.Any(entry =>
                        string.Equals(entry.ExePath, path, StringComparison.OrdinalIgnoreCase));

                    if (exists)
                    {
                        continue;
                    }

                    _entries.Add(new AppEntry
                    {
                        ExePath = path,
                        Priority = FixedPriority.Normal,
                        RunOnStartupWithLock = false,
                        RuntimeStatus = "Not running"
                    });
                    added++;
                }

                SaveSettings();
                if (added > 0)
                {
                    SetInfo(string.Format("Added {0} {1}.", added, EntryWord(added)));
                    ClearGridSelection();
                }
                else
                {
                    SetInfo("No entries added (duplicates).");
                }
            }
        }

        private void RemoveButton_Click(object sender, EventArgs e)
        {
            List<AppEntry> selectedEntries = GetSelectedEntries();
            if (selectedEntries.Count == 0)
            {
                SetInfo("Select entries to remove.", true);
                return;
            }

            bool hasRunning = selectedEntries.Any(entry => _processService.IsManaged(entry.Id));
            if (hasRunning)
            {
                DialogResult result = MessageBox.Show(
                    this,
                    "Some selected entries are currently tracked. Remove them from the list anyway?",
                    "Remove entries",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result != DialogResult.Yes)
                {
                    return;
                }
            }

            foreach (AppEntry entry in selectedEntries)
            {
                _processService.ReleaseEntry(entry);
                _entries.Remove(entry);
            }

            SaveSettings();
            SetInfo(string.Format("Removed {0} {1}.", selectedEntries.Count, EntryWord(selectedEntries.Count)));
            UpdateButtonsState();
        }

        private void StartButton_Click(object sender, EventArgs e)
        {
            List<AppEntry> selectedEntries = GetSelectedEntries();
            if (selectedEntries.Count == 0)
            {
                SetInfo("Select at least one entry to start.", true);
                return;
            }

            bool needsAdmin = selectedEntries.Any(entry =>
                entry.Priority == FixedPriority.High || entry.Priority == FixedPriority.Realtime);

            if (needsAdmin && !_elevationService.IsAdministrator)
            {
                DialogResult result = MessageBox.Show(
                    this,
                    "High/Realtime priorities require administrator rights.\nRestart PriorityControl as administrator now?",
                    "Administrator rights required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    string restartError;
                    if (_elevationService.TryRestartElevated(_launchArgs, out restartError))
                    {
                        Close();
                        return;
                    }

                    SetInfo(restartError, true);
                    return;
                }

                SetInfo("Start canceled: administrator rights were not granted.", true);
                return;
            }

            int startedCount = 0;
            var errors = new StringBuilder();

            foreach (AppEntry entry in selectedEntries)
            {
                string startError;
                if (_processService.StartWithFixedPriority(entry, out startError))
                {
                    startedCount++;
                }
                else
                {
                    errors.AppendLine(Path.GetFileName(entry.ExePath) + ": " + startError);
                }
            }

            RefreshAllStatuses();
            SaveSettings();

            if (errors.Length > 0)
            {
                MessageBox.Show(this, errors.ToString(), "Start errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            SetInfo(string.Format("Started {0} {1} with fixed priority.", startedCount, EntryWord(startedCount)));
        }

        private void LockButton_Click(object sender, EventArgs e)
        {
            List<AppEntry> selectedEntries = GetSelectedEntries();
            if (selectedEntries.Count == 0)
            {
                SetInfo("Select entries to lock.", true);
                return;
            }

            bool needsAdmin = selectedEntries.Any(entry =>
                entry.Priority == FixedPriority.High || entry.Priority == FixedPriority.Realtime);

            if (needsAdmin && !_elevationService.IsAdministrator)
            {
                SetInfo("High/Realtime lock requires administrator rights.", true);
                return;
            }

            int locked = 0;
            var errors = new StringBuilder();

            foreach (AppEntry entry in selectedEntries)
            {
                string lockError;
                if (_processService.ApplyPriorityLock(entry, out lockError))
                {
                    locked++;
                }
                else
                {
                    errors.AppendLine(Path.GetFileName(entry.ExePath) + ": " + lockError);
                }
            }

            RefreshAllStatuses();

            if (errors.Length > 0)
            {
                MessageBox.Show(this, errors.ToString(), "Lock errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            SetInfo(string.Format("Applied priority lock for {0} {1}.", locked, EntryWord(locked)));
        }

        private void UnlockButton_Click(object sender, EventArgs e)
        {
            List<AppEntry> selectedEntries = GetSelectedEntries();
            if (selectedEntries.Count == 0)
            {
                SetInfo("Select entries to unlock.", true);
                return;
            }

            int unlocked = 0;
            var errors = new StringBuilder();

            foreach (AppEntry entry in selectedEntries)
            {
                string unlockError;
                if (_processService.RemovePriorityLock(entry, out unlockError))
                {
                    unlocked++;
                }
                else
                {
                    errors.AppendLine(Path.GetFileName(entry.ExePath) + ": " + unlockError);
                }
            }

            RefreshAllStatuses();

            if (errors.Length > 0)
            {
                MessageBox.Show(this, errors.ToString(), "Unlock errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            SetInfo(string.Format("Removed priority lock for {0} {1}.", unlocked, EntryWord(unlocked)));
        }

        private void RestartAsAdminButton_Click(object sender, EventArgs e)
        {
            if (_elevationService.IsAdministrator)
            {
                SetInfo("Already running as administrator.");
                return;
            }

            string restartError;
            if (_elevationService.TryRestartElevated(_launchArgs, out restartError))
            {
                Close();
                return;
            }

            SetInfo(restartError, true);
        }

        private void StartWithWindowsCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            if (_isInitializing)
            {
                return;
            }

            try
            {
                _startupService.SetEnabled(_startWithWindowsCheckBox.Checked, Application.ExecutablePath);
                SaveSettings();
                SetInfo(_startWithWindowsCheckBox.Checked
                    ? "PriorityControl will start with Windows."
                    : "PriorityControl startup disabled.");
            }
            catch (Exception ex)
            {
                bool intendedValue = _startWithWindowsCheckBox.Checked;
                _isInitializing = true;
                _startWithWindowsCheckBox.Checked = !intendedValue;
                _isInitializing = false;
                SetInfo("Failed to update startup setting: " + ex.Message, true);
            }
        }

        private void Grid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void Grid_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            _isGridEditing = true;
        }

        private void Grid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            _isGridEditing = false;
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
            {
                return;
            }

            AppEntry entry = _grid.Rows[e.RowIndex].DataBoundItem as AppEntry;
            if (entry == null)
            {
                return;
            }

            string dataProperty = _grid.Columns[e.ColumnIndex].DataPropertyName;
            if (dataProperty == PropertyPriority && entry.IsPriorityLocked)
            {
                string updateError;
                if (!_processService.UpdateLockedPriority(entry, out updateError))
                {
                    SetInfo(updateError, true);
                }
            }

            SaveSettings();

            if (!_isGridEditing)
            {
                _processService.RefreshStatus(entry);
            }
        }

        private void RefreshAllStatuses()
        {
            _processService.RefreshStatuses(_entries);

            UpdateAdminState();
            UpdateButtonsState();
            _grid.Invalidate();
        }

        private void StartConfiguredStartupEntries()
        {
            List<AppEntry> startupEntries = _entries
                .Where(entry => entry.RunOnStartupWithLock)
                .ToList();

            if (startupEntries.Count == 0)
            {
                SetInfo("Startup mode: no configured entries to launch.");
                return;
            }

            bool requiresAdmin = startupEntries.Any(entry =>
                entry.Priority == FixedPriority.High || entry.Priority == FixedPriority.Realtime);

            if (requiresAdmin && !_elevationService.IsAdministrator)
            {
                string elevationError;
                if (_elevationService.TryRestartElevated(_launchArgs, out elevationError))
                {
                    Close();
                    return;
                }

                SetInfo("Startup mode failed to elevate: " + elevationError, true);
                return;
            }

            int started = 0;
            var errors = new StringBuilder();

            foreach (AppEntry entry in startupEntries)
            {
                bool needsAdmin = entry.Priority == FixedPriority.High || entry.Priority == FixedPriority.Realtime;
                if (needsAdmin && !_elevationService.IsAdministrator)
                {
                    errors.AppendLine(
                        Path.GetFileName(entry.ExePath) +
                        ": requires administrator rights for " +
                        entry.Priority +
                        ".");
                    continue;
                }

                string startError;
                if (_processService.StartWithFixedPriority(entry, out startError))
                {
                    started++;
                }
                else
                {
                    errors.AppendLine(Path.GetFileName(entry.ExePath) + ": " + startError);
                }
            }

            RefreshAllStatuses();

            if (errors.Length == 0)
            {
                SetInfo(string.Format("Startup mode: launched {0} {1}.", started, EntryWord(started)));
            }
            else
            {
                SetInfo("Startup mode completed with warnings. Open PriorityControl to review.");
            }
        }

        private List<AppEntry> GetSelectedEntries()
        {
            var selected = new List<AppEntry>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                AppEntry entry = row.DataBoundItem as AppEntry;
                if (entry != null && !selected.Contains(entry))
                {
                    selected.Add(entry);
                }
            }

            if (selected.Count == 0 && _grid.CurrentRow != null)
            {
                AppEntry currentEntry = _grid.CurrentRow.DataBoundItem as AppEntry;
                if (currentEntry != null)
                {
                    selected.Add(currentEntry);
                }
            }

            return selected;
        }

        private void UpdateAdminState()
        {
            bool isAdmin = _elevationService.IsAdministrator;
            _adminModeLabel.Text = isAdmin
                ? "Administrator mode: ON"
                : "Administrator mode: OFF";
            _adminModeLabel.ForeColor = isAdmin ? Color.FromArgb(25, 110, 48) : Color.DarkRed;
        }

        private void UpdateButtonsState()
        {
            bool hasSelection = GetSelectedEntries().Count > 0;
            _removeButton.Enabled = hasSelection;
            _startButton.Enabled = hasSelection;
            _lockButton.Enabled = hasSelection;
            _unlockButton.Enabled = hasSelection;
        }

        private void SetInfo(string message, bool isError = false)
        {
            _infoLabel.ForeColor = isError ? Color.DarkRed : Color.FromArgb(30, 30, 30);
            _infoLabel.Text = message;
        }

        private static string EntryWord(int count)
        {
            return count == 1 ? "entry" : "entries";
        }

        private void ClearGridSelection()
        {
            _grid.ClearSelection();
            _grid.CurrentCell = null;
            UpdateButtonsState();
        }
    }
}
