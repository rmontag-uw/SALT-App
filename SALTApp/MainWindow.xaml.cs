using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.Threading;
using System.Windows.Media.Imaging;
using libSALT.FunctionGeneratorAPI;
using libSALT.OscilloscopeAPI;
using libSALT;
using System.IO;

namespace SALTApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            calibration = false;
            uploading = false;
            loading = false;


            openingFile = false; // set the uploading flag

            functionGeneratorChannelInFocus = 1;  // start with channel 1 in focus
            fileDataMap = new Dictionary<string, WaveformFile>();
            currentWaveform = new WaveformFile();
            channelsPlaying = new HashSet<int>();
            cancelToken = new CancellationTokenSource();
            AutoResetEvent autoEvent = new AutoResetEvent(false);

            // Generate configuration file if one does not exist:    (replace this in a future update to be more stable and make more sense)
            if (!File.Exists("config.cfg"))
            {
                File.WriteAllText("config.cfg", "#Replace USB with ENET to use ethernet connectivity, do not include spaces and do not remove this line!\nINTERFACE=USB");
                    // a bit sketchy looking, but it works!
            }

            string[] configInput = File.ReadAllLines("config.cfg");
            string interfaceConfig;
            if (configInput.Length > 0)  // if the file is just empty for some reason, use USB
            {
                interfaceConfig = configInput[1];
                if (interfaceConfig.Equals("INTERFACE=ENET"))  // don't crash on invalid config files (in the future alert the user), just use USB
                {
                    interfaceConfig = "ENET";
                } else
                {
                    interfaceConfig = "USB";
                }
            } else
            {
                interfaceConfig = "USB";
            }
            if (interfaceConfig.Equals("ENET"))
            {
                ENET_Constructor();  // aaaaaa
            } else
            {
                USB_Constructor(); 
            }
            // at the end of both options, scope and fg are set and initialized for I/O operations



            // now we will read in that config file to see whether to use USB connection or over Ethernet, which means bringing up a seperate window

          
           
            idealNumScreenPoints = scope.GetNumPointsPerScreenCapture();  // for graphing purposes
            oscilloscopeNumHorizDiv = scope.GetNumHorizontalDivisions();
            oscilloscopeNumVertDiv = scope.GetNumVerticalDivisions();
            scope.Run();  // start the scope
            fg.SetAllOutputsOff();  // turn off all the outputs of the function generator
            scopeChannelInFocus = 1;  // start with channel 1 in focus for the scope
            InitializeComponent();
            LogoImage.Source = new BitmapImage(new Uri("logo.png", UriKind.Relative));
            WaveformLoadMessage.Visibility = Visibility.Hidden;  // hide the "waveform loading please wait" message
            WaveformUploadMessage.Visibility = Visibility.Hidden;  // hide the "uploading waveform please wait" message
                                                                   //  waveformGraph.Background = Brushes.Black;  // set the canvas background to black
            WaveformSampleRate.IsReadOnly = true;  // make the sample rate textbox read only by default
            WaveformAmplitudeScaleFactor.IsReadOnly = true;   // make the scale factor textbox read only
            cancelFileOpen.Visibility = Visibility.Hidden;  // hide the button for canceling file upload
            UploadWaveform.IsEnabled = false;  // disable the button for uploading a waveform
            EditWaveformParameterCheckbox.IsEnabled = false;  // disable the edit waveform parameter checkbox
            LoadWaveformButton.IsEnabled = false; // disable the button for loading a waveform into active memory
            WaveformSampleRate.IsEnabled = false;
            WaveformAmplitudeScaleFactor.IsEnabled = false;
            OffsetErrorLabel.Visibility = Visibility.Hidden;  // hide the error label
            AmplitudeErrorLabel.Visibility = Visibility.Hidden;  // hide the amplitude error label
            OffsetErrorLabel.Foreground = Brushes.Red;  // make the error text red
            AmplitudeErrorLabel.Foreground = Brushes.Red;
            ScalingErrorLabel.Visibility = Visibility.Hidden;
            ScalingErrorLabel.Foreground = Brushes.Red;  // make the error text red
            PlayWaveform.IsEnabled = false;  // disable the button for playing a waveform
            SaveWaveformParameters.IsEnabled = false; // disable the save waveform parameters button
            WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the instruction label for saving waveform
            voltageOffsetScaleConstant = scope.GetYAxisOffsetScaleConstant();
            triggerPositionScaleConstant = scope.GetTriggerPositionScaleConstant();  // get the graph constants from the scope
            timeOffsetScaleConstant = scope.GetXAxisOffsetScaleConstant();
            SavingWaveformCaptureLabel.Visibility = Visibility.Hidden; // hide the "saving waveform please wait" label
            showTriggerLine = false;  // start by not showing the dashed line for the trigger
            double tempYScale = scope.GetYScale();
            VoltageOffsetSlider.Maximum = voltageOffsetScaleConstant * tempYScale;  // set the voltage slider max and min to whatever the max and min
            VoltageOffsetSlider.Minimum = -1 * VoltageOffsetSlider.Maximum;  // are when we boot up the scope
            TriggerSlider.Maximum = triggerPositionScaleConstant * tempYScale;  // trigger slider needs the same range as the offset slider
            TriggerSlider.Minimum = -1 * TriggerSlider.Maximum;
            numOscilloscopeChannels = scope.GetNumChannels();
            channelsToDisable = new HashSet<int>();
            channelsToDraw = new HashSet<int>();  // if a channel number is contained within this list, draw it
            channelsToDraw.Add(1);  // we enable graphing channel 1 by default
            channelGraphs = new LineSeries[numOscilloscopeChannels];  // init the channelGraphs arrray
            voltageAxes = new LinearAxis[numOscilloscopeChannels];  // get an axis for each input channel of the scope 
            FGWaveformPlot.Model = new PlotModel();
            FGWaveformGraphDataLine = new LineSeries() { Color = OxyColor.FromRgb(34, 139, 34) };
            FGWaveformGraphZeroLine = new LineSeries() { Color = OxyColor.FromRgb(0, 0, 0) };  // zero line is black
            FGWaveformPlot.Model.Series.Add(FGWaveformGraphZeroLine);
            FGWaveformPlot.Model.Series.Add(FGWaveformGraphDataLine);
            channelColors = new System.Drawing.Color[numOscilloscopeChannels];
            mappedVoltageScales = scope.GetVoltageScalePresets();
            mappedTimeScales = scope.GetTimeScalePresets();
            for (int i = 0; i < numOscilloscopeChannels; i++)  // this application does not respond to runtime changes in channel color
                                                               // I have never heard of a scope that does that but just for reference.
            {
                channelColors[i] = scope.GetChannelColor(i + 1);  // we save the channel colors so we don't need to access them again
            }
            foreach (string s in scope.GetVoltageScalePresetStrings())  // add all supported voltage scale presets to the combobox of voltage scales
            {
                VoltageScalePresetComboBox.Items.Add(s);
            }
            foreach (string s in scope.GetTimeScalePresetStrings())  // add all supported time scale presets to the combobox of time scales
            {
                TimeScalePresetComboBox.Items.Add(s);
            }
            MemoryDepthComboBox.Items.Add("AUTO");  // add the auto option first
            int[] tempAllowedMemDepths = scope.GetAllowedMemDepths();
            foreach (int i in tempAllowedMemDepths)  // add all supported memory depths to the combobox of memory depths
            {
                MemoryDepthComboBox.Items.Add(i);
            }

            // on startup set the displayed memory depth to be the current selected mem depth for the scope
            for (int i = 1; i <= numOscilloscopeChannels; i++)  // add the everything for the different channels into the different stackpanels
            {
                scope.DisableChannel(i);  // just make sure everything is set to off before we start
                Label channelLabel = new Label() { Content = i + "=" + scope.GetYScale(i) + "V", Visibility = Visibility.Hidden };
                // get the scale labels for each channel, and then hide them
                CheckBox cb = new CheckBox() { Content = "Channel " + i, IsChecked = false};
                RadioButton rb = new RadioButton()
                {
                    Content = "Channel " + i + "   ",
                    IsChecked = false,
                    FlowDirection = FlowDirection.RightToLeft,
                };
                rb.Checked += (sender, args) =>  // on channel change, switch the displayed scale to the scale for that channel
                {
                    int checkedChannel = (int)(sender as RadioButton).Tag;
                    scopeChannelInFocus = checkedChannel;
                    double offset = scope.GetYAxisOffset(scopeChannelInFocus);  // and set the displayed offset to the offset of that channel
                    VoltageOffsetSlider.Value = offset;
                    previousYScaleFactor = scope.GetYScale(scopeChannelInFocus);
                    int channelChangedVoltageScaleCheck = Array.IndexOf(mappedVoltageScales, previousYScaleFactor);
                    if (channelChangedVoltageScaleCheck >= 0)
                    {  // use the mapping between names and actual scales to set the initial selected scale to the one
                       // the scope is presently showing for channel 1.
                        VoltageScalePresetComboBox.SelectedIndex = channelChangedVoltageScaleCheck;
                    }
                };
                rb.Unchecked += (sender, args) => { };  // currently no events need to be triggered on an un-check
                rb.Tag = i;
                OscilloscopeRadioButtonStackPanel.Children.Add(rb);
                cb.Checked += (sender, args) =>
                {
                    int checkedChannel = (int)(sender as CheckBox).Tag;
                    channelsToDraw.Add(checkedChannel);  // enable drawing of the checked channel
                    scope.EnableChannel(checkedChannel);
                    channelsToDisable.Remove(checkedChannel);
                    (OscilloscopeChannelScaleStackPanel.Children[checkedChannel - 1] as Label).Visibility = Visibility.Visible;
                    // when checked/enabled, show the scale for the channel
                    // when a channel is enabled or disabled, we'll need to check if we need to adjust the possible memory depth values
                    MemoryDepthComboBox.Items.Clear();  // first clear out the current iteams
                    MemoryDepthComboBox.Items.Add("AUTO");
                    int[] updatedMemDepths = scope.GetAllowedMemDepths();
                    foreach (int memDepth in updatedMemDepths)
                    {
                        MemoryDepthComboBox.Items.Add(memDepth);  // then add back in the new ones
                    }
                    OScope_CheckMemoryDepth(updatedMemDepths);

                };
                cb.Unchecked += (sender, args) =>
                {
                    int uncheckedChannel = (int)(sender as CheckBox).Tag;
                    channelsToDraw.Remove(uncheckedChannel);  // disable drawing of the unchecked channel
                    scope.DisableChannel(uncheckedChannel);
                    WaveformPlot.Model.Series.Remove(channelGraphs[uncheckedChannel - 1]);  // when the user disables the waveform, clear what remains
                    WaveformPlot.Model.InvalidatePlot(true);
                    channelsToDisable.Add(uncheckedChannel);
                    (OscilloscopeChannelScaleStackPanel.Children[uncheckedChannel - 1] as Label).Visibility = Visibility.Hidden;
                    // when unchecked/disabled, hide the scale for the channel
                    // when a channel is enabled or disabled, we'll need to check if we need to adjust the possible memory depth values
                    MemoryDepthComboBox.Items.Clear();  // first clear out the current iteams

                    MemoryDepthComboBox.Items.Add("AUTO");
                    int[] updatedMemDepths = scope.GetAllowedMemDepths();
                    foreach (int memDepth in updatedMemDepths)
                    {
                        MemoryDepthComboBox.Items.Add(memDepth);  // then add back in the new ones
                    }
                    OScope_CheckMemoryDepth(updatedMemDepths);

                };
                cb.Tag = i;
                OscilloscopeChannelButtonStackPanel.Children.Add(cb);
                OscilloscopeChannelScaleStackPanel.Children.Add(channelLabel);  // add the channel label to the stackpanel
            }
            (OscilloscopeChannelButtonStackPanel.Children[0] as CheckBox).IsChecked = true;  // set channel 1 to be checked by default on startup
            (OscilloscopeRadioButtonStackPanel.Children[0] as RadioButton).IsChecked = true;  // set channel 1 to be checked by default on startup
            (OscilloscopeChannelScaleStackPanel.Children[0] as Label).Visibility = Visibility.Visible;  // show channel 1's scale as well.
            scope.EnableChannel(1);
            double currentYScale = scope.GetYScale(1);  // we have tempYScale and currentYScale hmmmm
            previousYScaleFactor = currentYScale;
            int currentVoltageScaleCheck = Array.IndexOf(mappedVoltageScales, currentYScale);
            if (currentVoltageScaleCheck >= 0)
            {  // use the mapping between names and actual scales to set the initial selected scale to the one
                // the scope is presently showing for channel 1.
                VoltageScalePresetComboBox.SelectedIndex = currentVoltageScaleCheck;
            }
            int currentTimeScaleCheck = Array.IndexOf(mappedTimeScales, scope.GetXAxisScale());
            if (currentTimeScaleCheck >= 0)
            {  // use the mapping between names and actual scales to set the initial selected scale to the one
                // the scope is presently showing for channel 1.
                TimeScalePresetComboBox.SelectedIndex = currentTimeScaleCheck;
            }
            OScope_CheckMemoryDepth(tempAllowedMemDepths);
            WaveformPlot.Model = new PlotModel { Title = "Oscilloscope Capture" };


            foreach (string memoryLocation in fg.GetValidMemoryLocations())
            {
                fileDataMap.Add(memoryLocation, new WaveformFile());  // put placeholder blank waveforms in the fileDataMap
                WaveformList.Items.Add(memoryLocation);  // add each valid memory location to the list box
            }
            for (int i = 1; i <= fg.GetNumChannels(); i++)
            {
                RadioButton rb = new RadioButton() { Content = "Channel " + i, IsChecked = i == 0 };
                rb.Checked += (sender, args) =>
                {
                    int checkedChannel = (int)(sender as RadioButton).Tag;
                    functionGeneratorChannelInFocus = checkedChannel;
                    if (channelsPlaying.Contains(checkedChannel))
                    {
                        PlayWaveform.Content = "Restart Waveform";
                    }
                    else
                    {
                        PlayWaveform.Content = "Play Waveform";
                    }
                    FG_ChannelChanged();
                };
                rb.Unchecked += (sender, args) => { };  // currently no events need to be triggered on an un-check
                rb.Tag = i;
                FunctionGenChannelButtonStackPanel.Children.Add(rb);
            }
            (FunctionGenChannelButtonStackPanel.Children[0] as RadioButton).IsChecked = true;  // set channel 1 to be checked by default
            if (maximumAllowedAmplitude <= 0 || maximumAllowedAmplitude > fg.GetMaxSupportedVoltage() - fg.GetMinSupportedVoltage())
            // if the maximumAllowedAmplitude setting is below (or equals for sanity checking) 0, OR it's set to something too large then
            // we just set it to be the maximum allowed amplitude of the function generator itself.
            {
                maximumAllowedAmplitude = fg.GetMaxSupportedVoltage() - fg.GetMinSupportedVoltage();
            }
            // these get forced into being symmetrical. That is okay in my opinion. I don't think there are any function generators with
            // non-symmetrical voltage limits

            Color backgroundColor = (Color)Background.GetValue(SolidColorBrush.ColorProperty);
            OxyColor hiddenColor = OxyColor.FromArgb(0, backgroundColor.R, backgroundColor.G, backgroundColor.B);

            // This axis is the voltage axis for the FG waveform display
            LinearAxis FGWaveformVoltageAxis = new LinearAxis
            {
                Minimum = -1 * maximumAllowedAmplitude / 2,
                // set the graph's max and min labels to be what the used function generator's max and min supported voltages actually are.
                Maximum = maximumAllowedAmplitude / 2,
                IsPanEnabled = false,
                IsZoomEnabled = false,
                Position = AxisPosition.Left,
                Title = "Voltage"
            };

            // This axis is just here to make sure that the bottom axis has no ticks or number labels 
            LinearAxis FGWaveformBottomAxis = new LinearAxis
            {

                IsPanEnabled = false,
                IsZoomEnabled = false,
                IsAxisVisible = false,
                TickStyle = TickStyle.None,
                TextColor = hiddenColor,  // makes it look rectangular at startup while still having no visible numbers on the bottom. 
                Position = AxisPosition.Bottom
            };
            FGWaveformPlot.Model.Axes.Add(FGWaveformVoltageAxis);
            FGWaveformPlot.Model.Axes.Add(FGWaveformBottomAxis);


            // These Axes form the background grid of the oscope display.


            LinearAxis horizGridAxis1 = new LinearAxis  // make horizontal grid lines
            {
                Position = AxisPosition.Left,  // radiate out from center bar to the left
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                IsPanEnabled = false,
                MajorStep = idealNumScreenPoints / oscilloscopeNumVertDiv,
                IsZoomEnabled = false,
                Minimum = -1 * (idealNumScreenPoints / 2),   // point 0 is centered, so we have a range of -600 to 600 or similar
                Maximum = (idealNumScreenPoints / 2),
                AbsoluteMinimum = -1*(idealNumScreenPoints / 2),
                AbsoluteMaximum = (idealNumScreenPoints/2),
                MinimumPadding = 0,
                MaximumPadding = 0,
                TicklineColor = OxyColor.FromRgb(0, 0, 0),
                PositionAtZeroCrossing = true,
                TextColor = hiddenColor,
            };
            LinearAxis horizGridAxis2 = new LinearAxis  // make horizontal grid lines
            {
                Position = AxisPosition.Right,  // radiate out of center bar to the right
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                IsPanEnabled = false,
                MajorStep = idealNumScreenPoints / oscilloscopeNumVertDiv,
                IsZoomEnabled = false,
                Minimum = -1 * (idealNumScreenPoints / 2),   // point 0 is centered, so we have a range of -600 to 600 or similar
                Maximum = (idealNumScreenPoints / 2),
                AbsoluteMinimum = -1 * (idealNumScreenPoints / 2),
                AbsoluteMaximum = (idealNumScreenPoints / 2),
                IntervalLength = idealNumScreenPoints / oscilloscopeNumHorizDiv,
                MinimumPadding = 0,
                MaximumPadding = 0,
                TicklineColor = OxyColor.FromRgb(0, 0, 0),
                PositionAtZeroCrossing = true,
                TextColor = hiddenColor,
                ExtraGridlines = new double[] { 0 }


            };
            LinearAxis vertGridAxis1 = new LinearAxis
            {
                Position = AxisPosition.Top,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                MajorStep = Width/oscilloscopeNumHorizDiv,
                TicklineColor = OxyColor.FromRgb(0, 0, 0),
                PositionAtZeroCrossing = true,
                MinimumPadding = 0,
                MaximumPadding = 0,
                IsZoomEnabled = false,
                IsPanEnabled = false,
                TextColor = hiddenColor,
                ExtraGridlines = new double[] { 0 }

            };
            LinearAxis vertGridAxis2 = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                MajorStep = Width / oscilloscopeNumHorizDiv,
                TicklineColor = OxyColor.FromRgb(0, 0, 0),
                PositionAtZeroCrossing = true,
                MinimumPadding = 0,
                MaximumPadding = 0,
                IsZoomEnabled = false,
                IsPanEnabled = false,
                TextColor = hiddenColor,
            };
            for (int i = 0; i < channelGraphs.Length; i++)
            {
                channelGraphs[i] = new LineSeries();  // then init the objects in it
                voltageAxes[i] = new LinearAxis
                {  // just like on the actual scope display, we need to hide our axes
                    Position = AxisPosition.Left,
                    TicklineColor = hiddenColor,
                    TextColor = hiddenColor,
                    IsZoomEnabled = false,
                    IsPanEnabled = false,
                    Key = (i + 1).ToString(),  // make the key the channel number (it's fine the text is clear)
                };
                WaveformPlot.Model.Axes.Add(voltageAxes[i]);  // a lot of array accesses here, possibly redo with temp variables?
                channelGraphs[i].YAxisKey = voltageAxes[i].Key;  // then use that to associate the axis with the channel graph

            }

            WaveformPlot.Model.Axes.Add(horizGridAxis1); // add the left to right grid line axis to the display
            WaveformPlot.Model.Axes.Add(vertGridAxis1);
            WaveformPlot.Model.Axes.Add(horizGridAxis2);
            WaveformPlot.Model.Axes.Add(vertGridAxis2);
            triggerLine = new LineSeries();
            // the orange trigger set line must be drawn on the graph
            double triggerLineScaled = (scope.GetTriggerLevel() * currentYScale) - (currentYScale / 2);
            WaveformPlot.Model.Series.Add(triggerLine);
            WaveformPlot.Model.InvalidatePlot(true);
        }

        private void USB_Constructor()  // if we're using a USB interface (or really anything VISA compatible but not LAN)
        {
            // this is one of those situations where C#'s ability to use "var" is nice, but statically typed until the end!
            VISADevice.ConnectedDeviceStruct<VISAOscilloscope> oscilloscopes = VISAOscilloscope.GetConnectedOscilloscopes();
            VISADevice.ConnectedDeviceStruct<VISAFunctionGenerator> functionGenerators = VISAFunctionGenerator.GetConnectedFunctionGenerators();
            if (oscilloscopes.connectedDevices.Length == 0)
            {
                // show a messagebox error if there are no scopes connected
                MessageBoxResult result = MessageBox.Show("Error: No oscilloscopes found. " +
                    "Make sure that the oscilloscope is connected and turned on and then try again",
                    appName, MessageBoxButton.OK);
                switch (result)
                {
                    case MessageBoxResult.OK:  // there's only one case
                        ExitAll();  // might cause annoying task cancelled errors, we'll have to deal with those
                        return;
                }
            }
            if (functionGenerators.connectedDevices.Length == 0)
            {
                // show a messagebox error if there are no function generators connected
                MessageBoxResult result = MessageBox.Show("Error: No function generators found. " +
                    "Make sure that the function generator is connected and turned on and then try again",
                    appName, MessageBoxButton.OK);
                switch (result)
                {
                    case MessageBoxResult.OK:  // there's only one case
                        ExitAll();  // might cause annoying task cancelled errors, we'll have to deal with those
                        return;
                }
            }
            if (oscilloscopes.connectedDevices.Length > 1)
            {
                MessageBoxResult result = MessageBox.Show("Error: Too many oscilloscopes found. " +
                   "Unplug all but one oscilloscope and then try again",
                   appName, MessageBoxButton.OK);
                switch (result)
                {
                    case MessageBoxResult.OK:  // there's only one case
                        ExitAll();  // might cause annoying task cancelled errors, we'll have to deal with those
                        return;
                }
            }
            else
            {
                scope = oscilloscopes.connectedDevices[0];  // there's only one, that's the one we use
            }
            if (functionGenerators.connectedDevices.Length > 1)
            {
                MessageBoxResult result = MessageBox.Show("Error: Too many function generators found. " +
                   "Unplug all but one function generator and then try again",
                   appName, MessageBoxButton.OK);
                switch (result)
                {
                    case MessageBoxResult.OK:  // there's only one case
                        ExitAll();  // might cause annoying task cancelled errors, we'll have to deal with those
                        return;
                }
            }
            else
            {
                fg = functionGenerators.connectedDevices[0];  // there's only one, that's the one we use
            }

        }

        private void ENET_Constructor()
        {
            string FG_IP = "";
            string OSCOPE_IP = "";
            // #Saved IP Addresses, do not edit
            // FGIP=0.0.0.0
            // OSIP=0.0.0.0
            if (File.Exists("enet.cfg"))
            {
                string[] configInput = File.ReadAllLines("enet.cfg");
                if (configInput.Length < 3)
                {
                    // someone messed with the config file >:(
                    FG_IP = "0.0.0.0";
                    OSCOPE_IP = "0.0.0.0"; // set to default IP 
                } else
                {
                    FG_IP = configInput[1].Substring(5);  // get the actual IP strings from config file
                    OSCOPE_IP = configInput[2].Substring(5);
                }
            }
            SaltApp.IPInputWindow IPWindow = new SaltApp.IPInputWindow();
            IPWindow.FunctionGenIPInputBox.Text = FG_IP;
            IPWindow.OscilloscopeIPInputBox.Text = OSCOPE_IP;
            IPWindow.ShowDialog();
            FG_IP = IPWindow.FunctionGenIPInputBox.Text;
            OSCOPE_IP = IPWindow.OscilloscopeIPInputBox.Text;  // get the results after the user clicked okay

            File.WriteAllText("enet.cfg", "#Saved IP Addresses, do not edit\nFGIP=" + FG_IP + "\nOSIP=" + OSCOPE_IP);  // write the user's specified IP addresses
            // to the file, where they can be read from again.

            Console.WriteLine(FG_IP);
            string FG_VISA = "TCPIP0::" + FG_IP + "::inst0::INSTR";  // generate the two VISA ids for devices at the given IP addresses
            string OSCOPE_VISA = "TCPIP0::" + OSCOPE_IP + "::inst0::INSTR";
            IFunctionGenerator tempFG = VISAFunctionGenerator.TryOpen(FG_VISA);  // attempt to open the devices at the specified IP addresses
            IOscilloscope tempScope = VISAOscilloscope.TryOpen(OSCOPE_VISA);  
            if(tempFG == null)
            {
                MessageBoxResult result = MessageBox.Show("Error: Could not open function generator at " + FG_IP,
                   appName, MessageBoxButton.OK);
                switch (result)
                {
                    case MessageBoxResult.OK:  // there's only one case
                        ExitAll();  // let the user try again without exiting probably, but we'll do that logic in a sec
                        return;
                }
            }
            if (tempScope == null)
            {
                MessageBoxResult result = MessageBox.Show("Error: Could not open oscilloscope at " + OSCOPE_IP,
                   appName, MessageBoxButton.OK);
                switch (result)
                {
                    case MessageBoxResult.OK:  // there's only one case
                        ExitAll();  // let the user try again without exiting probably, but we'll do that logic in a sec
                        return;
                }
            }
            // if we made it down here, both the scope and function generator are initialized, so we can set the global variables and return from this function
            fg = tempFG;
            scope = tempScope;
            // yay!
        }

        private void ExitAll()
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();  // a bit brutal but it works
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //InitializeFG();  // after the window is loaded, attempt to initialize the function generator
            FG_DrawWaveformGraph(null);  // just to draw the zero line on the function gen graph before startup
            OScope_SetRefreshTimer();
        }
    }
}
