using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Inventor;
using ThreadModeler;
using ThreadModeler.Utilities;
using TextBox = System.Windows.Forms.TextBox;

namespace ThreadModeler.Commands
{
    internal class PrintController : Form
    {
        private Inventor.Application _Application;
        private PartDocument _Document;
        private AdnInteractionManager _InteractionManager;
        private PrintThreadContext _Context;
        private PrintThreadPreset _Preset;
        private bool _Cleaned;
        private bool _UpdatingFields;
        private bool _FieldsValid;
        private bool _PresetDirty;
        private System.Windows.Forms.Timer _SelectionTimer;
        private int _SelectionFingerprint;

        private readonly Label lbStatus;
        private readonly TextBox tbThread;
        private readonly TextBox tbNominalDiameter;
        private readonly TextBox tbUsefulLength;
        private readonly TextBox tbThreadType;
        private readonly TextBox tbFaceType;
        private readonly TextBox tbPresetName;
        private readonly TextBox tbBaseWidth;
        private readonly TextBox tbTopWidth;
        private readonly TextBox tbHeight;
        private readonly TextBox tbPitch;
        private readonly TextBox tbClearance;
        private readonly Button bApplyPreset;
        private readonly Button bGenerate;
        private readonly Button bCancel;

        public PrintController(
            Inventor.Application Application,
            AdnInteractionManager InteractionManager)
        {
            _Application = Application;
            _InteractionManager = InteractionManager;
            _Document = _Application.ActiveEditDocument as PartDocument;

            Text = "3D print custom thread";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AutoSize = false;
            AutoSizeMode = AutoSizeMode.GrowOnly;
            AutoScroll = true;
            ClientSize = new System.Drawing.Size(480, 540);
            StartPosition = FormStartPosition.CenterParent;
            Padding = new Padding(12);

            lbStatus = new Label();
            tbThread = CreateReadOnlyBox();
            tbNominalDiameter = CreateReadOnlyBox();
            tbUsefulLength = CreateReadOnlyBox();
            tbThreadType = CreateReadOnlyBox();
            tbFaceType = CreateReadOnlyBox();
            tbPresetName = CreateReadOnlyBox();
            tbBaseWidth = CreateEditableBox();
            tbTopWidth = CreateEditableBox();
            tbHeight = CreateEditableBox();
            tbPitch = CreateEditableBox();
            tbClearance = CreateEditableBox();
            bApplyPreset = new Button();
            bGenerate = new Button();
            bCancel = new Button();

            BuildUi();

            tbBaseWidth.TextChanged += NumericField_TextChanged;
            tbTopWidth.TextChanged += NumericField_TextChanged;
            tbHeight.TextChanged += NumericField_TextChanged;
            tbPitch.TextChanged += NumericField_TextChanged;
            tbClearance.TextChanged += NumericField_TextChanged;
            bApplyPreset.Click += bApplyPreset_Click;
            bGenerate.Click += bGenerate_Click;
            bCancel.Click += bCancel_Click;

            Shown += PrintController_Shown;
            FormClosing += PrintController_FormClosing;
            Activated += PrintController_Activated;

            _SelectionTimer = new System.Windows.Forms.Timer();
            _SelectionTimer.Interval = 250;
            _SelectionTimer.Tick += SelectionTimer_Tick;

            if (_InteractionManager != null &&
                _InteractionManager.SelectEvents != null)
            {
                _InteractionManager.SelectEvents.OnSelect +=
                    new SelectEventsSink_OnSelectEventHandler(SelectEvents_OnSelect);

                _InteractionManager.SelectEvents.OnUnSelect +=
                    new SelectEventsSink_OnUnSelectEventHandler(SelectEvents_OnUnSelect);
            }

            PrintThreadWorker.Initialize(_Application);
        }

        private void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.AutoSize = false;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Dock = DockStyle.Fill;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            Label intro = new Label();
            intro.AutoSize = true;
            intro.Text = "Select one ThreadFeature in the browser, then adjust the print preset.";
            intro.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(intro, 0, 0);

            GroupBox gbSelection = new GroupBox();
            gbSelection.Text = "Selection";
            gbSelection.Dock = DockStyle.Fill;
            gbSelection.Padding = new Padding(12, 18, 12, 12);
            root.Controls.Add(gbSelection, 0, 1);

            TableLayoutPanel selectionGrid = CreateGrid(2, 5);
            gbSelection.Controls.Add(selectionGrid);
            AddRow(selectionGrid, 0, "Thread", tbThread);
            AddRow(selectionGrid, 1, "Nominal diameter (mm)", tbNominalDiameter);
            AddRow(selectionGrid, 2, "Useful length (mm)", tbUsefulLength);
            AddRow(selectionGrid, 3, "Thread type", tbThreadType);
            AddRow(selectionGrid, 4, "Face type", tbFaceType);

            GroupBox gbPreset = new GroupBox();
            gbPreset.Text = "3D print custom thread preset";
            gbPreset.Dock = DockStyle.Fill;
            gbPreset.Padding = new Padding(12, 18, 12, 12);
            root.Controls.Add(gbPreset, 0, 2);

            TableLayoutPanel presetGrid = CreateGrid(3, 6);
            gbPreset.Controls.Add(presetGrid);
            bApplyPreset.Text = "Reset preset";
            bApplyPreset.Width = 75;
            AddRow(presetGrid, 0, "Preset", tbPresetName, bApplyPreset);
            AddRow(presetGrid, 1, "Base width (mm)", tbBaseWidth);
            AddRow(presetGrid, 2, "Top width (mm)", tbTopWidth);
            AddRow(presetGrid, 3, "Height (mm)", tbHeight);
            AddRow(presetGrid, 4, "Pitch (mm)", tbPitch);
            AddRow(presetGrid, 5, "Clearance (mm)", tbClearance);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 10, 0, 0);

            bGenerate.Text = "Generate";
            bGenerate.Width = 90;
            bGenerate.Enabled = false;

            bCancel.Text = "Cancel";
            bCancel.Width = 90;

            buttons.Controls.Add(bCancel);
            buttons.Controls.Add(bGenerate);
            root.Controls.Add(buttons, 0, 3);

            lbStatus.AutoSize = true;
            lbStatus.ForeColor = System.Drawing.Color.DimGray;
            lbStatus.Text = "Waiting for a thread selection.";
            lbStatus.Margin = new Padding(0, 0, 0, 8);
            root.Controls.Add(lbStatus, 0, 4);

            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        }

        private static TableLayoutPanel CreateGrid(int columns, int rows)
        {
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.AutoSize = false;
            grid.ColumnCount = columns;
            grid.RowCount = rows;
            grid.Dock = DockStyle.Fill;
            grid.Padding = new Padding(0);
            grid.Margin = new Padding(0);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            if (columns == 3)
            {
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            }

            return grid;
        }

        private static TextBox CreateReadOnlyBox()
        {
            return new TextBox
            {
                ReadOnly = true,
                Dock = DockStyle.Fill,
                MinimumSize = new System.Drawing.Size(180, 22)
            };
        }

        private static TextBox CreateEditableBox()
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                MinimumSize = new System.Drawing.Size(90, 22)
            };
        }

        private void AddRow(
            TableLayoutPanel grid,
            int row,
            string label,
            Control control)
        {
            AddRow(grid, row, label, control, null);
        }

        private void AddRow(
            TableLayoutPanel grid,
            int row,
            string label,
            Control control,
            Control extraControl)
        {
            Label caption = new Label();
            caption.AutoSize = true;
            caption.Text = label;
            caption.Margin = new Padding(0, 6, 8, 0);

            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(caption, 0, row);
            grid.Controls.Add(control, 1, row);

            if (extraControl != null)
            {
                extraControl.Margin = new Padding(8, 0, 0, 0);
                grid.Controls.Add(extraControl, 2, row);
            }
        }

        private void PrintController_Shown(object sender, EventArgs e)
        {
            if (_SelectionTimer != null)
            {
                _SelectionTimer.Start();
            }

            RefreshSelectionFromDocument();
            UpdateButtons();
        }

        private void PrintController_Activated(object sender, EventArgs e)
        {
            RefreshSelectionFromDocument();
        }

        private void PrintController_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_Cleaned)
            {
                CleanUp();
            }
        }

        private void SelectEvents_OnSelect(
            ObjectsEnumerator JustSelectedEntities,
            SelectionDeviceEnum SelectionDevice,
            Point ModelPosition,
            Point2d ViewPosition,
            Inventor.View View)
        {
            RefreshSelectionFromDocument();
        }

        private void SelectEvents_OnUnSelect(
            ObjectsEnumerator UnSelectedEntities,
            SelectionDeviceEnum SelectionDevice,
            Point ModelPosition,
            Point2d ViewPosition,
            Inventor.View View)
        {
            RefreshSelectionFromDocument();
        }

        private void SelectionTimer_Tick(object sender, EventArgs e)
        {
            RefreshSelectionFromDocument();
        }

        private void RefreshSelectionFromDocument()
        {
            int fingerprint = ComputeSelectionFingerprint();
            if (fingerprint == _SelectionFingerprint && _Context != null)
            {
                return;
            }

            _SelectionFingerprint = fingerprint;
            _Context = null;
            string firstError = string.Empty;
            int selectionCount = 0;

            if (_Document == null)
            {
                lbStatus.Text = "Active document is not a Part document.";
                ClearSelectionFields();
                UpdateButtons();
                return;
            }

            try
            {
                System.Collections.Generic.List<object> candidates = GetSelectedCandidates();
                selectionCount = candidates.Count;

                for (int i = 0; i < candidates.Count; i++)
                {
                    string errorMessage;
                    PrintThreadContext context;
                    if (PrintThreadWorker.TryBuildContext(candidates[i], _Document, out context, out errorMessage))
                    {
                        _Context = context;
                        break;
                    }

                    if (string.IsNullOrEmpty(firstError) && !string.IsNullOrEmpty(errorMessage))
                    {
                        firstError = errorMessage;
                    }
                }
            }
            catch
            {
                _Context = null;
            }

            if (_Context == null)
            {
                lbStatus.Text = (selectionCount > 0 && !string.IsNullOrEmpty(firstError))
                    ? firstError
                    : "Select one ThreadFeature in the browser.";
                ClearSelectionFields();
                UpdateButtons();
                return;
            }

            PopulateFieldsFromContext();
            if (!_PresetDirty)
            {
                ApplyPresetFromContext();
            }
            else
            {
                lbStatus.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "Selected {0} with nominal size {1}. Custom values kept.",
                    tbThread.Text,
                    _Context.NominalLabel);
            }
        }

        private System.Collections.Generic.List<object> GetSelectedCandidates()
        {
            System.Collections.Generic.List<object> candidates = new System.Collections.Generic.List<object>();

            try
            {
                if (_InteractionManager != null)
                {
                    foreach (object obj in _InteractionManager.SelectedEntities)
                    {
                        if (obj != null && !candidates.Contains(obj))
                        {
                            candidates.Add(obj);
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (_Document != null)
                {
                    SelectSet selectSet = _Document.SelectSet;
                    for (int i = 1; i <= selectSet.Count; i++)
                    {
                        object obj = selectSet[i];
                        if (obj != null && !candidates.Contains(obj))
                        {
                            candidates.Add(obj);
                        }
                    }
                }
            }
            catch
            {
            }

            return candidates;
        }

        private int ComputeSelectionFingerprint()
        {
            int hash = 17;
            try
            {
                if (_Document != null)
                {
                    SelectSet selectSet = _Document.SelectSet;
                    hash = hash * 31 + selectSet.Count;
                    for (int i = 1; i <= selectSet.Count; i++)
                    {
                        object obj = selectSet[i];
                        hash = hash * 31 + (obj == null ? 0 : RuntimeHelpers.GetHashCode(obj));
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (_InteractionManager != null)
                {
                    hash = hash * 31 + _InteractionManager.SelectedEntities.Count;
                    foreach (object obj in _InteractionManager.SelectedEntities)
                    {
                        hash = hash * 31 + (obj == null ? 0 : RuntimeHelpers.GetHashCode(obj));
                    }
                }
            }
            catch
            {
            }

            return hash;
        }

        private void PopulateFieldsFromContext()
        {
            _UpdatingFields = true;

            tbThread.Text = _Context.ThreadFeature == null ? string.Empty : _Context.ThreadFeature.Name;
            tbNominalDiameter.Text = FormatCmAsMm(_Context.NominalDiameterCm);
            tbUsefulLength.Text = FormatCmAsMm(_Context.UsefulLengthCm);
            tbThreadType.Text = ThreadWorker.GetThreadTypeStr(_Context.Feature);
            tbFaceType.Text = ThreadWorker.GetThreadedFaceTypeStr(_Context.ThreadedFace);
            lbStatus.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Selected {0} with nominal size {1}.",
                tbThread.Text,
                _Context.NominalLabel);

            _UpdatingFields = false;
        }

        private void ApplyPresetFromContext()
        {
            if (_Context == null)
            {
                return;
            }

            _Preset = PrintThreadWorker.BuildPreset(_Context);

            _UpdatingFields = true;
            tbPresetName.Text = _Preset.Name;
            tbBaseWidth.Text = FormatCmAsMm(_Preset.BaseWidthCm);
            tbTopWidth.Text = FormatCmAsMm(_Preset.TopWidthCm);
            tbHeight.Text = FormatCmAsMm(_Preset.HeightCm);
            tbPitch.Text = FormatCmAsMm(_Preset.PitchCm);
            tbClearance.Text = "0";
            _UpdatingFields = false;
            _PresetDirty = false;

            ValidateEditableFields();
            UpdateButtons();
        }

        private void ClearSelectionFields()
        {
            _UpdatingFields = true;
            tbThread.Text = string.Empty;
            tbNominalDiameter.Text = string.Empty;
            tbUsefulLength.Text = string.Empty;
            tbThreadType.Text = string.Empty;
            tbFaceType.Text = string.Empty;
            tbPresetName.Text = string.Empty;
            tbBaseWidth.Text = string.Empty;
            tbTopWidth.Text = string.Empty;
            tbHeight.Text = string.Empty;
            tbPitch.Text = string.Empty;
            tbClearance.Text = string.Empty;
            tbBaseWidth.ForeColor = System.Drawing.Color.Black;
            tbTopWidth.ForeColor = System.Drawing.Color.Black;
            tbHeight.ForeColor = System.Drawing.Color.Black;
            tbPitch.ForeColor = System.Drawing.Color.Black;
            tbClearance.ForeColor = System.Drawing.Color.Black;
            _UpdatingFields = false;
            _FieldsValid = false;
            _PresetDirty = false;
        }

        private void NumericField_TextChanged(object sender, EventArgs e)
        {
            if (_UpdatingFields)
            {
                return;
            }

            _PresetDirty = true;
            ValidateEditableFields();
            UpdateButtons();
        }

        private void ValidateEditableFields()
        {
            double baseWidth;
            double topWidth;
            double height;
            double pitch;
            double clearance;

            bool baseValid = TryReadMmAsCm(tbBaseWidth.Text, out baseWidth);
            bool topValid = TryReadMmAsCm(tbTopWidth.Text, out topWidth);
            bool heightValid = TryReadMmAsCm(tbHeight.Text, out height);
            bool pitchValid = TryReadMmAsCm(tbPitch.Text, out pitch);
            bool clearanceValid = TryReadMmAsCm(tbClearance.Text, out clearance) && clearance >= 0.0;

            SetFieldState(tbBaseWidth, baseValid);
            SetFieldState(tbTopWidth, topValid);
            SetFieldState(tbHeight, heightValid);
            SetFieldState(tbPitch, pitchValid);
            SetFieldState(tbClearance, clearanceValid);

            bool lengthValid = true;
            bool profileValid = false;
            bool clearanceRangeValid = false;
            bool heightClearanceValid = false;
            bool pitchWidthValid = false;

            if (baseValid && topValid && heightValid && pitchValid && clearanceValid &&
                baseWidth > 0.0 && topWidth > 0.0 && height > 0.0 && pitch > 0.0 &&
                clearance >= 0.0 && topWidth < baseWidth)
            {
                double effectiveBaseWidth = baseWidth - clearance;
                double effectiveTopWidth = topWidth - clearance;
                double effectiveHeight = height - (clearance * 0.5);

                clearanceRangeValid = clearance < topWidth * 0.8;
                heightClearanceValid = effectiveHeight > ThreadWorker.ThresholdPitchCm;
                pitchWidthValid = pitch >= effectiveBaseWidth * 0.75;
                profileValid = effectiveBaseWidth > 0.0 &&
                    effectiveTopWidth > 0.0 &&
                    effectiveTopWidth < effectiveBaseWidth &&
                    clearanceRangeValid &&
                    heightClearanceValid &&
                    pitchWidthValid;
            }

            if (_Context != null && _Context.IsInteriorFace &&
                baseValid && topValid && pitchValid && clearanceValid &&
                profileValid)
            {
                double effectiveBaseWidth = baseWidth - clearance;
                double effectiveTopWidth = topWidth - clearance;
                double minWidth = Math.Min(Math.Max(pitch * 0.02, 0.002), pitch * 0.20);
                double baseWidthComplement = Math.Max(minWidth, pitch - effectiveTopWidth);
                double topWidthComplement = Math.Max(minWidth, pitch - effectiveBaseWidth);
                double axialWidth = Math.Max(baseWidthComplement, topWidthComplement);
                lengthValid = _Context.UsefulLengthCm - axialWidth >= pitch;
            }

            _FieldsValid = baseValid && topValid && heightValid && pitchValid && clearanceValid &&
                baseWidth > 0.0 && topWidth > 0.0 && height > 0.0 && pitch > 0.0 &&
                clearance >= 0.0 &&
                profileValid &&
                lengthValid &&
                _Context != null;
        }

        private void SetFieldState(TextBox tb, bool valid)
        {
            tb.ForeColor = valid ? System.Drawing.Color.Black : System.Drawing.Color.Red;
        }

        private void bApplyPreset_Click(object sender, EventArgs e)
        {
            ApplyPresetFromContext();
        }

        private void bGenerate_Click(object sender, EventArgs e)
        {
            if (_Context == null)
            {
                MessageBox.Show(
                    "Select one ThreadFeature first.",
                    "Missing Selection",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            double baseWidth;
            double topWidth;
            double height;
            double pitch;
            double clearance;

            if (!TryReadMmAsCm(tbBaseWidth.Text, out baseWidth) ||
                !TryReadMmAsCm(tbTopWidth.Text, out topWidth) ||
                !TryReadMmAsCm(tbHeight.Text, out height) ||
                !TryReadMmAsCm(tbPitch.Text, out pitch) ||
                !TryReadMmAsCm(tbClearance.Text, out clearance))
            {
                MessageBox.Show(
                    "Fix the profile values before generating the print thread.",
                    "Invalid Parameters",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            PrintThreadPreset preset = new PrintThreadPreset
            {
                Name = tbPresetName.Text,
                BaseWidthCm = baseWidth,
                TopWidthCm = topWidth,
                HeightCm = height,
                PitchCm = pitch,
                ClearanceCm = clearance
            };

            string errorMessage;
            if (!PrintThreadWorker.ModelizeThreadPrint(_Document, _Context, preset, out errorMessage))
            {
                MessageBox.Show(
                    string.IsNullOrEmpty(errorMessage) ? "Failed to generate the 3D print thread." : errorMessage,
                    "Modelization Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _PresetDirty = false;

            if (!_Cleaned)
            {
                CleanUp();
            }

            _InteractionManager.Terminate();
        }

        private void bCancel_Click(object sender, EventArgs e)
        {
            if (!_Cleaned)
            {
                CleanUp();
            }

            _InteractionManager.Terminate();
        }

        private void UpdateButtons()
        {
            bGenerate.Enabled = (_Context != null && _FieldsValid);
            bApplyPreset.Enabled = (_Context != null);
        }

        private static bool TryReadMmAsCm(string text, out double value)
        {
            double valueMm;
            bool parsed = double.TryParse(
                              text,
                              NumberStyles.Float,
                              CultureInfo.CurrentCulture,
                              out valueMm)
                          || double.TryParse(
                              text,
                              NumberStyles.Float,
                              CultureInfo.InvariantCulture,
                              out valueMm);

            value = parsed ? valueMm * 0.1 : 0.0;
            return parsed;
        }

        private static string FormatCmAsMm(double valueCm)
        {
            return (valueCm * 10.0).ToString("0.###", CultureInfo.CurrentCulture);
        }

        private void CleanUp()
        {
            if (_Cleaned)
            {
                return;
            }

            try
            {
                if (_InteractionManager != null &&
                    _InteractionManager.SelectEvents != null)
                {
                    _InteractionManager.SelectEvents.OnSelect -=
                        new SelectEventsSink_OnSelectEventHandler(SelectEvents_OnSelect);

                    _InteractionManager.SelectEvents.OnUnSelect -=
                        new SelectEventsSink_OnUnSelectEventHandler(SelectEvents_OnUnSelect);
                }
            }
            catch
            {
            }

            Shown -= PrintController_Shown;
            Activated -= PrintController_Activated;
            FormClosing -= PrintController_FormClosing;
            if (_SelectionTimer != null)
            {
                _SelectionTimer.Stop();
                _SelectionTimer.Tick -= SelectionTimer_Tick;
                _SelectionTimer.Dispose();
                _SelectionTimer = null;
            }
            _Cleaned = true;
        }
    }
}
