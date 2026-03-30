using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System.ComponentModel;
using NewAxis.Services;

namespace NewAxis
{
    public partial class MeshWindow : Window
    {
        public MeshWindow()
        {
            InitializeComponent();
            SetupInteractions();
            ApplyLocalizedTexts();
            LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
            Closed += (_, _) => LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
            WindowState = WindowState.FullScreen;
            SystemDecorations = SystemDecorations.None;
            Width = 3840;
            Height = 2160;
        }

        private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LocalizationService.CurrentLanguage) || e.PropertyName == "Item[]" || e.PropertyName == "Item")
            {
                ApplyLocalizedTexts();
            }
        }

        private void ApplyLocalizedTexts()
        {
            var loc = LocalizationService.Instance;
            Title = loc["Viewer3DWindowTitle"];

            this.FindControl<TextBlock>("ViewerTitleText")?.SetCurrentValue(TextBlock.TextProperty, loc["Viewer3DTitle"]);
            this.FindControl<TextBlock>("ViewerControlsText")?.SetCurrentValue(TextBlock.TextProperty, loc["ViewerControls"]);
            this.FindControl<TextBlock>("ScaleLabelText")?.SetCurrentValue(TextBlock.TextProperty, loc["Scale"]);
            this.FindControl<TextBlock>("DepthLabelText")?.SetCurrentValue(TextBlock.TextProperty, loc["Depth"]);
            this.FindControl<TextBlock>("PopoutLabelText")?.SetCurrentValue(TextBlock.TextProperty, loc["Popout"]);
            this.FindControl<TextBlock>("ParallaxLabelText")?.SetCurrentValue(TextBlock.TextProperty, loc["Parallax"]);

            this.FindControl<Button>("LoadButton")?.SetCurrentValue(Button.ContentProperty, loc["LoadModel"]);
            this.FindControl<CheckBox>("RotateCheckBox")?.SetCurrentValue(CheckBox.ContentProperty, loc["AutoRotate"]);
            this.FindControl<CheckBox>("DitheredBlendCheckBox")?.SetCurrentValue(CheckBox.ContentProperty, loc["DitheredTransparency"]);
            this.FindControl<CheckBox>("ShowStageCheckBox")?.SetCurrentValue(CheckBox.ContentProperty, loc["ShowStage"]);
            this.FindControl<TextBlock>("BackgroundColorLabelText")?.SetCurrentValue(TextBlock.TextProperty, loc["BackgroundColor"]);
            this.FindControl<Button>("ResetButton")?.SetCurrentValue(Button.ContentProperty, loc["ResetView"]);
        }

        private void SetupInteractions()
        {
            var viewer = this.FindControl<NewAxis.Controls.MeshViewerControl>("Viewer3D");
            var depthSlider = this.FindControl<Slider>("DepthSlider");
            var popoutSlider = this.FindControl<Slider>("PopoutSlider");
            var parallaxSlider = this.FindControl<Slider>("ParallaxSlider");

            var scaleSpinner = this.FindControl<NumericUpDown>("ScaleSpinner");

            var rotateCheckBox = this.FindControl<CheckBox>("RotateCheckBox");
            var ditheredBlendCheckBox = this.FindControl<CheckBox>("DitheredBlendCheckBox");
            var showStageCheckBox = this.FindControl<CheckBox>("ShowStageCheckBox");
            var backgroundColorPicker = this.FindControl<ColorPicker>("BackgroundColorPicker");
            var resetButton = this.FindControl<Button>("ResetButton");
            var closeButton = this.FindControl<Button>("CloseButton");

            var mainGrid = this.FindControl<Grid>("MainGrid");
            var uiControls = this.FindControl<StackPanel>("UIControls");

            if (rotateCheckBox != null && viewer != null)
            {
                rotateCheckBox.PropertyChanged += (s, e) =>
                {
                    if (e.Property == CheckBox.IsCheckedProperty)
                    {
                        viewer.AutoRotate = rotateCheckBox.IsChecked ?? false;
                    }
                };
            }

            if (ditheredBlendCheckBox != null && viewer != null)
            {
                viewer.UseDitheredBlend = ditheredBlendCheckBox.IsChecked ?? false;
                ditheredBlendCheckBox.PropertyChanged += (s, e) =>
                {
                    if (e.Property == CheckBox.IsCheckedProperty)
                    {
                        viewer.UseDitheredBlend = ditheredBlendCheckBox.IsChecked ?? false;
                    }
                };
            }

            if (showStageCheckBox != null && viewer != null && backgroundColorPicker != null)
            {
                viewer.ShowStage = showStageCheckBox.IsChecked ?? true;
                viewer.BackgroundColor = backgroundColorPicker.Color;
                
                showStageCheckBox.PropertyChanged += (s, e) =>
                {
                    if (e.Property == CheckBox.IsCheckedProperty)
                    {
                        bool isChecked = showStageCheckBox.IsChecked ?? true;
                        viewer.ShowStage = isChecked;
                        backgroundColorPicker.IsEnabled = !isChecked; // Enable picker when stage is disabled
                    }
                };
            }

            if (backgroundColorPicker != null && viewer != null)
            {
                backgroundColorPicker.PropertyChanged += (s, e) =>
                {
                    if (e.Property == ColorPicker.ColorProperty)
                    {
                        viewer.BackgroundColor = backgroundColorPicker.Color;
                    }
                };
            }

            if (closeButton != null)
            {
                closeButton.Click += (s, e) => this.Close();
            }

            if (resetButton != null && viewer != null)
            {
                resetButton.Click += (s, e) =>
                {
                    viewer.ResetCamera();
                    if (depthSlider != null) depthSlider.Value = 0.18;
                    if (popoutSlider != null) popoutSlider.Value = 0.03;
                    if (parallaxSlider != null) parallaxSlider.Value = 0.48;
                    if (scaleSpinner != null) scaleSpinner.Value = 1.0m;
                };
            }

            if (viewer != null)
            {
                var depthText = this.FindControl<TextBlock>("DepthValueText");
                if (depthSlider != null)
                {
                    depthSlider.PropertyChanged += (s, e) =>
                    {
                        if (e.Property == Slider.ValueProperty)
                        {
                            viewer.StereoSeparation = (float)(depthSlider.Value);
                            if (depthText != null)
                            {
                                int percent = (int)((depthSlider.Value - depthSlider.Minimum) / (depthSlider.Maximum - depthSlider.Minimum) * 100);
                                depthText.Text = $"{percent}%";
                            }
                        }
                    };
                }

                var popoutText = this.FindControl<TextBlock>("PopoutValueText");
                if (popoutSlider != null)
                {
                    popoutSlider.PropertyChanged += (s, e) =>
                    {
                        if (e.Property == Slider.ValueProperty)
                        {
                            viewer.StereoConvergence = (float)(popoutSlider.Value);
                            if (popoutText != null)
                            {
                                int percent = (int)((popoutSlider.Value - popoutSlider.Minimum) / (popoutSlider.Maximum - popoutSlider.Minimum) * 100);
                                popoutText.Text = $"{percent}%";
                            }
                        }
                    };
                }

                var parallaxText = this.FindControl<TextBlock>("ParallaxValueText");
                // var parallaxSlider = this.FindControl<Slider>("ParallaxSlider"); // Removed, declared above
                if (parallaxSlider != null)
                {
                    parallaxSlider.PropertyChanged += (s, e) =>
                    {
                        if (e.Property == Slider.ValueProperty)
                        {
                            viewer.ParallaxIntensity = (float)(parallaxSlider.Value);
                            if (parallaxText != null)
                            {
                                int percent = (int)((parallaxSlider.Value - parallaxSlider.Minimum) / (parallaxSlider.Maximum - parallaxSlider.Minimum) * 100);
                                parallaxText.Text = $"{percent}%";
                            }
                        }
                    };
                }

                if (scaleSpinner != null)
                {
                    scaleSpinner.ValueChanged += (s, e) =>
                    {
                        // Dynamic increment: if value <= 0.2, use 0.01, else 0.1
                        var val = scaleSpinner.Value ?? 1.0m;
                        if (val <= 0.2m) scaleSpinner.Increment = 0.01m;
                        else scaleSpinner.Increment = 0.1m;

                        viewer.Scale = (float)val;
                    };
                }
            }

            // --- Mouse Interaction & Auto Hide ---
            if (mainGrid != null && viewer != null && uiControls != null)
            {
                bool isDragging = false;
                Point lastPos = new Point(0, 0);

                // Timer for Auto-Hide
                var hideTimer = new Avalonia.Threading.DispatcherTimer();
                hideTimer.Interval = System.TimeSpan.FromSeconds(3);
                hideTimer.Tick += (s, e) =>
                {
                    uiControls.Opacity = 0.0;
                    hideTimer.Stop();
                };
                hideTimer.Start();

                mainGrid.PointerPressed += (s, e) =>
                {
                    isDragging = true;
                    lastPos = e.GetPosition(mainGrid);

                    // Show UI on click
                    uiControls.Opacity = 1.0;
                    hideTimer.Stop();
                    // Don't restart immediately on press, wait for move or release
                };

                mainGrid.PointerReleased += (s, e) =>
                {
                    isDragging = false;

                    // Restart hide timer
                    hideTimer.Stop();
                    hideTimer.Start();
                };

                mainGrid.PointerWheelChanged += (s, e) =>
                {
                    // Zoom: Mouse Wheel
                    // Sensitivity 0.5f
                    if (viewer != null)
                    {
                        viewer.MoveZ += (float)e.Delta.Y * 0.2f;
                    }

                    // Restart hide timer
                    if (uiControls.Opacity < 0.9)
                    {
                        uiControls.Opacity = 1.0;
                    }
                    hideTimer.Stop();
                    hideTimer.Start();
                };

                mainGrid.PointerMoved += (s, e) =>
                {
                    var currentPos = e.GetPosition(mainGrid);

                    // always show UI when moving
                    if (uiControls.Opacity < 0.9)
                    {
                        uiControls.Opacity = 1.0;
                    }
                    hideTimer.Stop();
                    hideTimer.Start();

                    if (isDragging)
                    {
                        var deltaX = (float)(currentPos.X - lastPos.X);
                        var deltaY = (float)(currentPos.Y - lastPos.Y);

                        var props = e.GetCurrentPoint(mainGrid).Properties;

                        if (props.IsLeftButtonPressed)
                        {
                            // Rotate: Left Drag
                            // Sensitivity
                            viewer.Yaw += deltaX * 0.01f;
                            viewer.Pitch += deltaY * 0.01f;
                        }
                        else if (props.IsRightButtonPressed)
                        {
                            // Move: Right Drag
                            // Screen space to World space approximation
                            viewer.MoveX += deltaX * 0.005f;
                            viewer.MoveY -= deltaY * 0.005f; // Invert Y for screen->world
                        }

                        lastPos = currentPos;
                    }
                };
            }
        }

        private async void LoadButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var storage = StorageProvider;
            var loc = LocalizationService.Instance;
            var result = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = loc["Open3DModel"],
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType(loc["Models3D"]) { Patterns = new[] { "*.obj", "*.glb" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(loc["ObjFiles"]) { Patterns = new[] { "*.obj" } },
                    new Avalonia.Platform.Storage.FilePickerFileType(loc["GlbFiles"]) { Patterns = new[] { "*.glb" } }
                }
            });

            if (result.Count > 0)
            {
                var file = result[0];
                var path = file.Path.LocalPath;
                try
                {
                    var viewer = this.FindControl<NewAxis.Controls.MeshViewerControl>("Viewer3D");
                    if (viewer == null) return;

                    string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".obj")
                    {
                        var mesh = NewAxis.Services.SimpleObjLoader.Load(path);
                        viewer.LoadMesh(mesh, System.IO.Path.GetDirectoryName(path) ?? "");
                    }
                    else if (ext == ".glb")
                    {
                        var glbData = NewAxis.Services.GlbLoader.Load(path);
                        viewer.LoadGlb(glbData);
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Failed to load mesh: " + ex);
                }
            }
        }
    }
}
