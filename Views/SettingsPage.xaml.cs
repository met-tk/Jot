using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Journal.Models;
using Journal.ViewModels;

namespace Journal.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; } = new();

        public SettingsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

            this.Loaded += (s, e) =>
            {
                ViewModel.Subscribe();
                UpdatePreviewTypography();
            };

            this.Unloaded += (s, e) =>
            {
                ViewModel.Unsubscribe();
            };

            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(ViewModel.SelectedFontFamily)
                    or nameof(ViewModel.FontSize)
                    or nameof(ViewModel.LineSpacing)
                    or nameof(ViewModel.SamplePreviewText))
                {
                    UpdatePreviewTypography();
                }
            };
        }

        private void UpdatePreviewTypography()
        {
            try
            {
                if (TypographyPreviewText != null)
                {
                    TypographyPreviewText.FontFamily = AppSettings.BuildXamlFontFamily(ViewModel.SelectedFontFamily);
                    TypographyPreviewText.FontSize = ViewModel.FontSize;
                    TypographyPreviewText.LineHeight = ViewModel.PreviewLineHeight;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdatePreviewTypography Error] {ex.Message}");
            }
        }

        private void ImportDiaries_Click(object sender, RoutedEventArgs e)
        {
            if (ShellPage.Current != null)
            {
                ShellPage.Current.SelectTab("Import");
            }
        }

        private void AuthorLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://space.bilibili.com/24340298")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AuthorLink_Click Error] {ex.Message}");
            }
        }

        private void FeedbackEmail_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("mailto:yunotaketo@gmail.com")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeedbackEmail_Click Error] {ex.Message}");
            }
        }
    }
}
