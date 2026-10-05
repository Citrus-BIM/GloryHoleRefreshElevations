using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace GloryHoleRefreshElevations
{
    public partial class GloryHoleRefreshElevationsWPF : Window
    {
        private const string RebindWarning = "После перепривязки проверьте привязку к уровням.";
        private GloryHoleRefreshElevationsSettings _settings;
        private bool _isInitialized;

        public string RoundHolesPositionButtonName;
        public double RoundHolePositionIncrement;
        public string RoundHolesLocationButtonName;
        public double RoundHoleLocationIncrement;
        public string RefreshElevationsOptionButtonName;

        // These options apply only to this command invocation and never enter the updater settings.
        public bool RebindToLevels { get; private set; }
        public bool RebindToSelectedLevel { get; private set; }
        public string SelectedRebindLevelUniqueId { get; private set; } = string.Empty;

        public GloryHoleRefreshElevationsWPF() : this(Array.Empty<RebindLevel>())
        {
        }

        internal GloryHoleRefreshElevationsWPF(IReadOnlyList<RebindLevel> levels)
        {
            _settings = GloryHoleRefreshElevationsSettings.GetSettings();
            InitializeComponent();
            MaxHeight = Math.Max(280, SystemParameters.WorkArea.Height);
            MinHeight = Math.Min(MinHeight, MaxHeight);

            comboBox_RebindLevel.ItemsSource = levels
                .OrderBy(level => level.ProjectElevation)
                .ThenBy(level => level.Name)
                .ToList();

            if (_settings != null)
            {
                rbt_SelectedItems.IsChecked = _settings.RefreshElevationsOptionButtonName == "rbt_SelectedItems";
                rbt_AllProject.IsChecked = rbt_SelectedItems.IsChecked != true;
                radioButton_RoundHolesPositionYes.IsChecked =
                    _settings.RoundHolesPositionButtonName == "radioButton_RoundHolesPositionYes";
                radioButton_RoundHolesPositionNo.IsChecked = radioButton_RoundHolesPositionYes.IsChecked != true;
                textBox_RoundHolePositionIncrement.Text =
                    string.IsNullOrWhiteSpace(_settings.RoundHolePositionIncrementValue)
                        ? "5" : _settings.RoundHolePositionIncrementValue;

                radioButton_RoundHolesLocationYes.IsChecked =
                    _settings.RoundHolesLocationButtonName == "radioButton_RoundHolesLocationYes";
                radioButton_RoundHolesLocationNo.IsChecked = radioButton_RoundHolesLocationYes.IsChecked != true;
                textBox_RoundHoleLocationIncrement.Text =
                    string.IsNullOrWhiteSpace(_settings.RoundHoleLocationIncrementValue)
                        ? "5" : _settings.RoundHoleLocationIncrementValue;
                checkBox_UpdaterOn.IsChecked = _settings.UpdaterOn;
            }

            _isInitialized = true;
            UpdateRoundingControls();
            UpdateRebindControls();
        }

        private void btn_Ok_Click(object sender, RoutedEventArgs e)
        {
            if (TrySaveSettings())
                DialogResult = true;
        }

        private void btn_Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void radioButton_RoundHolesPosition_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitialized)
                UpdateRoundingControls();
        }

        private void radioButton_RoundHolesLocation_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitialized)
                UpdateRoundingControls();
        }

        private void UpdateRoundingControls()
        {
            bool positionEnabled = radioButton_RoundHolesPositionYes.IsChecked == true;
            RoundHolesPositionButtonName = positionEnabled
                ? "radioButton_RoundHolesPositionYes" : "radioButton_RoundHolesPositionNo";
            label_RoundHolePosition.IsEnabled = positionEnabled;
            textBox_RoundHolePositionIncrement.IsEnabled = positionEnabled;
            label_RoundHolePositionMM.IsEnabled = positionEnabled;

            bool locationEnabled = radioButton_RoundHolesLocationYes.IsChecked == true;
            RoundHolesLocationButtonName = locationEnabled
                ? "radioButton_RoundHolesLocationYes" : "radioButton_RoundHolesLocationNo";
            label_RoundHoleLocation.IsEnabled = locationEnabled;
            textBox_RoundHoleLocationIncrement.IsEnabled = locationEnabled;
            label_RoundHoleLocationMM.IsEnabled = locationEnabled;
        }

        private void RefreshScope_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitialized)
                UpdateRebindControls();
        }

        private void RebindToLevels_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
                return;

            UpdateRebindControls();
            if (checkBox_RebindToLevels.IsChecked == true)
                MessageBox.Show(this, RebindWarning, "Перепривязка к уровням",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void RebindMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitialized)
                UpdateRebindControls();
        }

        private void UpdateRebindControls()
        {
            bool rebind = checkBox_RebindToLevels.IsChecked == true;
            bool canChooseLevel = rebind && rbt_SelectedItems.IsChecked == true;
            rbt_RebindAutomatically.IsEnabled = rebind;
            rbt_RebindSelectedLevel.IsEnabled = canChooseLevel;
            if (!canChooseLevel)
            {
                // The scope guard also normalizes stale/restored selections before acceptance.
                rbt_RebindAutomatically.IsChecked = true;
                comboBox_RebindLevel.SelectedIndex = -1;
            }

            bool manual = canChooseLevel && rbt_RebindSelectedLevel.IsChecked == true;
            panel_RebindLevel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
            comboBox_RebindLevel.IsEnabled = manual;
            textBlock_RebindWarning.Visibility = rebind ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool TrySaveSettings()
        {
            UpdateRoundingControls();
            UpdateRebindControls();
            double positionIncrement;
            if (!TryReadIncrement(textBox_RoundHolePositionIncrement,
                _settings == null ? null : _settings.RoundHolePositionIncrementValue, out positionIncrement))
                return false;

            double locationIncrement;
            if (!TryReadIncrement(textBox_RoundHoleLocationIncrement,
                _settings == null ? null : _settings.RoundHoleLocationIncrementValue, out locationIncrement))
                return false;

            bool rebind = checkBox_RebindToLevels.IsChecked == true;
            bool manual = rebind && rbt_SelectedItems.IsChecked == true
                && rbt_RebindSelectedLevel.IsChecked == true;
            RebindLevel selectedLevel = comboBox_RebindLevel.SelectedItem as RebindLevel;
            if (manual && selectedLevel == null)
            {
                MessageBox.Show(this, "Выберите уровень.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                comboBox_RebindLevel.Focus();
                return false;
            }

            RefreshElevationsOptionButtonName = rbt_SelectedItems.IsChecked == true
                ? "rbt_SelectedItems" : "rbt_AllProject";
            var settings = new GloryHoleRefreshElevationsSettings
            {
                RefreshElevationsOptionButtonName = RefreshElevationsOptionButtonName,
                RoundHolesPositionButtonName = RoundHolesPositionButtonName,
                RoundHolePositionIncrementValue = textBox_RoundHolePositionIncrement.Text,
                RoundHolesLocationButtonName = RoundHolesLocationButtonName,
                RoundHoleLocationIncrementValue = textBox_RoundHoleLocationIncrement.Text,
                UpdaterOn = checkBox_UpdaterOn.IsChecked == true
            };

            try
            {
                settings.SaveSettings();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                MessageBox.Show(this, "Не удалось сохранить настройки.\n" + exception.Message,
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            _settings = settings;
            RoundHolePositionIncrement = positionIncrement;
            RoundHoleLocationIncrement = locationIncrement;
            RebindToLevels = rebind;
            RebindToSelectedLevel = manual;
            SelectedRebindLevelUniqueId = manual ? selectedLevel.UniqueId : string.Empty;
            return true;
        }

        private bool TryReadIncrement(TextBox input, string previousValue, out double increment)
        {
            if (TryParsePositiveIncrement(input.Text, out increment))
            {
                // The automatic updater reads this XML using the current culture.
                input.Text = increment.ToString(CultureInfo.CurrentCulture);
                return true;
            }

            if (input.IsEnabled)
            {
                MessageBox.Show(this, "Шаг округления должен быть числом больше нуля.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                input.Focus();
                input.SelectAll();
                return false;
            }

            // A disabled rounding field must not block the operation or persist an invalid value.
            if (!TryParsePositiveIncrement(previousValue, out increment))
                increment = 5;
            input.Text = increment.ToString(CultureInfo.CurrentCulture);
            return true;
        }

        private static bool TryParsePositiveIncrement(string text, out double increment)
        {
            return double.TryParse((text ?? string.Empty).Trim().Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out increment)
                && !double.IsNaN(increment) && !double.IsInfinity(increment) && increment > 0;
        }
    }
}
