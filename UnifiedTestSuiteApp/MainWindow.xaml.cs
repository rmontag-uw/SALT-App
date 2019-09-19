using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NationalInstruments.Visa;
using OscilloscopeAPI;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.IO;
using System.Threading;
using System.Timers;
using FunctionGeneratorAPI;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace UnifiedTestSuiteApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string appName = "Test Suite";
        private const int refreshInterval = 100;  // the graph refresh interval in ms
        private static System.Timers.Timer refreshTimer;  // gotta make sure there's no ambiguity with the threading timer
        private readonly IOscilloscope scope;
        private TextWriter currentLogFile;  // a little bit gross, but overall actually a fine solution
        private int scopeChannelInFocus;
        private readonly HashSet<int> channelsToDisable;
        private readonly LineSeries[] channelGraphs;
        private LineSeries triggerLine;  // the orange line for the trigger value
        private double previousYScaleFactor;
        private readonly CancellationTokenSource cancelToken;  // to avoid errors on closing
        private readonly LinearAxis[] voltageAxes;  // not using the abstract Axis for the type on purpose, we don't want any non-linear axes here
        private readonly object graphLock = new object();
        private readonly object downloadLock = new object();
        private readonly double[] mappedVoltageScales;
        private readonly double[] mappedTimeScales;
        private readonly int numOscilloscopeChannels;
        private readonly HashSet<int> channelsToDraw;
        private bool showTriggerLine;  // whether we should show the dashed orange trigger line or not
        private bool drawGraph = true;
        private readonly double triggerPositionScaleConstant;
        private readonly double voltageOffsetScaleConstant;
        private readonly double timeOffsetScaleConstant;
        private readonly System.Drawing.Color[] channelColors;
        private readonly IFunctionGenerator fg;
        private LineSeries FGWaveformGraphZeroLine;
        private LineSeries FGWaveformGraphDataLine;
        private bool calibration;
        private int functionGeneratorChannelInFocus;
        private bool openingFile;  // true if the user is opening a file
        private string memoryLocationSelected;
        private readonly HashSet<int> channelsPlaying;
        private readonly double maximumAllowedAmplitude = 1;  // set to -1 to allow amplitudes up to the function generator's maximum
        // Because the IntitializeFG method is different than the constructor, we can't even make this readonly.
        // this value should NOT ever be changed at runtime, that would be insane. if it's -1 set it to what it needs to be in the
        // InitializeFG method and leave it at that.
        private bool uploading;
        private bool loading;
        // each memory location is mapped to a WaveformFile that represents the waveform saved there.
        private readonly Dictionary<string, WaveformFile> fileDataMap;  // a map from the function generator's valid memory locations to
        private WaveformFile currentWaveform;
        // WaveformFiles that contain data about the waveform stored there
        public MainWindow()
        {
            calibration = false;
            uploading = false;
            loading = false;


            openingFile = false; // set the uploading flag


            // MAKE SOMETHING THAT LETS THE USER TRY TO CONNECT AGAIN IF THE RESOURCE ISN'T FOUND INSTEAD OF CRASHING.
            functionGeneratorChannelInFocus = 1;  // start with channel 1 in focus
            fileDataMap = new Dictionary<string, WaveformFile>();
            currentWaveform = new WaveformFile();
            channelsPlaying = new HashSet<int>();
            cancelToken = new CancellationTokenSource();
            ResourceManager rm = new ResourceManager();
            var autoEvent = new AutoResetEvent(false);
            var oscilloscopes = VISAOscilloscope.GetConnectedOscilloscopes();
            var functionGenerators = VISAFunctionGenerator.GetConnectedFunctionGenerators();
            if(oscilloscopes.connectedOscilloscopes.Length == 0)
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
            if(functionGenerators.connectedFunctionGenerators.Length == 0)
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
            if(oscilloscopes.connectedOscilloscopes.Length > 1)
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
            } else
            {
                scope = oscilloscopes.connectedOscilloscopes[0];  // there's only one, that's the one we use
            }
            if (functionGenerators.connectedFunctionGenerators.Length > 1)
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
                fg = functionGenerators.connectedFunctionGenerators[0];  // there's only one, that's the one we use
            }
     
            scope.Run();  // start the scope
            fg.SetAllOutputsOff();  // turn off all the outputs of the function generator
            scopeChannelInFocus = 1;  // start with channel 1 in focus for the scope
            InitializeComponent();

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
            voltageOffsetScaleConstant = scope.GetVoltageOffsetScaleConstant();
            triggerPositionScaleConstant = scope.GetTriggerPositionScaleConstant();  // get the graph constants from the scope
            timeOffsetScaleConstant = scope.GetTimeOffsetScaleConstant();
            SavingWaveformCaptureLabel.Visibility = Visibility.Hidden; // hide the "saving waveform please wait" label
            showTriggerLine = false;  // start by not showing the dashed line for the trigger
            double tempYScale = scope.GetYScale();
            VoltageOffsetSlider.Maximum = voltageOffsetScaleConstant * tempYScale;
            VoltageOffsetSlider.Minimum = -1 * VoltageOffsetSlider.Maximum;
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
            FGWaveformGraphZeroLine = new LineSeries() { Color = OxyColor.FromRgb(0,0,0)};  // zero line is black
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
                CheckBox cb = new CheckBox() { Content = "Channel " + i, IsChecked = false };
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
                    double offset = scope.GetVerticalOffset(scopeChannelInFocus);  // and set the displayed offset to the offset of that channel
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
                    CheckMemoryDepth(updatedMemDepths);

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
                    CheckMemoryDepth(updatedMemDepths);

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
            int currentTimeScaleCheck = Array.IndexOf(mappedTimeScales, scope.GetTimeScale(1));
            if (currentTimeScaleCheck >= 0)
            {  // use the mapping between names and actual scales to set the initial selected scale to the one
                // the scope is presently showing for channel 1.
                TimeScalePresetComboBox.SelectedIndex = currentTimeScaleCheck;
            }
            CheckMemoryDepth(tempAllowedMemDepths);
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
                    ChannelChanged();
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
                Position = AxisPosition.Left,  // no actual axis bar on the side plz
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                IsPanEnabled = false,
                //MajorStep = WaveformPlot.Model.Height / 8,   finding a function for the correct MajorStep instead of just a hardcoded value
                // would be nice
                MajorStep = 12.5,
                IsZoomEnabled = false,
                MinimumPadding = 0,
                MaximumPadding = 0,
                TicklineColor = OxyColor.FromRgb(0, 0, 0),
                PositionAtZeroCrossing = true,
                TextColor = hiddenColor,
            };
            LinearAxis horizGridAxis2 = new LinearAxis  // make horizontal grid lines
            {
                Position = AxisPosition.Right,  // no actual axis bar on the side plz
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                IsPanEnabled = false,
                MajorStep = 12.5,
                IsZoomEnabled = false,
                MinimumPadding = 0,
                MaximumPadding = 0,
                TicklineColor = OxyColor.FromRgb(0, 0, 0),
                PositionAtZeroCrossing = true,
                TextColor = hiddenColor,


            };
            LinearAxis vertGridAxis1 = new LinearAxis
            {
                Position = AxisPosition.Top,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                MajorStep = 100,
                TicklineColor = OxyColor.FromRgb(0, 0, 0),
                PositionAtZeroCrossing = true,
                MinimumPadding = 0,
                MaximumPadding = 0,
                IsZoomEnabled = false,
                IsPanEnabled = false,
                TextColor = hiddenColor,

            };
            LinearAxis vertGridAxis2 = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                MajorStep = 100,
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

        private void ExitAll()
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();  // a bit brutal but it works
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //InitializeFG();  // after the window is loaded, attempt to initialize the function generator
            DrawFGWaveformGraph(null);  // just to draw the zero line on the function gen graph before startup
            SetRefreshTimer();
        }

       

        // FUNCTION GENERATOR STUFF


        // Draws the waveform stored in currentWaveform on the app's graph canvas
        private void DrawFGWaveformGraph()
        {
            DrawFGWaveformGraph(currentWaveform);
        }

        // Draws the waveform contained in the given WaveformFile on the app's graph canvas
        private void DrawFGWaveformGraph(WaveformFile wave)
        {
            int length;
            FGWaveformGraphDataLine.Points.Clear();
            if (wave == null || wave.FileName == null)  // if there's nothing actually saved in the current waveform, just draw the reference
                                                        // line for 0V and then return
            {
                FGWaveformGraphZeroLine.Points.Clear();
                FGWaveformGraphZeroLine.Points.Add(new DataPoint(0, 0));
                FGWaveformGraphZeroLine.Points.Add(new DataPoint(FGWaveformPlot.ActualWidth, 0));
                FGWaveformPlot.Model.InvalidatePlot(true);
                return;
            }
            if(wave.Voltages.Length > 1000)
            {
                length = 1000;
            } else
            {
                length = wave.Voltages.Length;
            }
            FGWaveformGraphZeroLine.Points.Clear();
            FGWaveformGraphZeroLine.Points.Add(new DataPoint(0, 0));
            FGWaveformGraphZeroLine.Points.Add(new DataPoint(length, 0));
            for (int i = 0; i < length; i++)
            {

                FGWaveformGraphDataLine.Points.Add(new DataPoint(i,wave.Voltages[i]));
              
            }
            FGWaveformPlot.Model.InvalidatePlot(true);
            if (openingFile)  // if we're in file opening mode
            {
                WaveformList.IsEnabled = true; // it's best just to wait until the graph is drawn.
            }
        }

        private void Button_Click_OpenWaveform(object sender, RoutedEventArgs e)
        {
            openingFile = true;
            WaveformList.IsEnabled = false;  // if the user attempts to click a memory location to save the waveform before it's actually
            // parsed and saved, the UI gets into a weird state where it is unable to actually 
            OpenFileDialog dlg = new OpenFileDialog
            {
                DefaultExt = ".txt",
                //|*.jpeg|PNG Files (*.png)|*.png|JPG Files (*.jpg)|*.jpg|GIF Files (*.gif)|*.gif
                Filter = "txt files (*.txt)|*.txt|All files (*.*)|*.*"  // filter shows txt files and other files if selected
            };  // open a file dialog
            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                string filePath = dlg.FileName;  // get the name of the file
                cancelFileOpen.Visibility = Visibility.Visible;  // show the button for canceling file upload
                OffsetErrorLabel.Visibility = Visibility.Hidden;  // hide the error label if it's showing
                AmplitudeErrorLabel.Visibility = Visibility.Hidden;  // hide the amplitude error label too
                ParseFileHandler(filePath);
                WaveformSaveInstructionLabel.Visibility = Visibility.Visible;  // show the instructions on how to save waveform
                //EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox

            }
        }

        private void Button_Click_PlayWaveform(object sender, RoutedEventArgs e)
        {
            channelsPlaying.Add(functionGeneratorChannelInFocus);
            // we only need to worry about what mode the function generator is in when the user hits play.
            // since this command is instant, we don't have to keep track of the current mode with a seperate thread.
            if (calibration)
            {
                fg.SetWaveformType(WaveformType.SINE, functionGeneratorChannelInFocus);  // set to sine wave mode for calibration waveforms
            }
            else
            {
                fg.SetWaveformType(WaveformType.ARB, functionGeneratorChannelInFocus);  // set to arbitrary wave mode for loaded waveforms.
                fg.SetSampleRate(currentWaveform.SampleRate, functionGeneratorChannelInFocus);  // set the sample rate. (and set Arb mode to TrueArb)
            }
            fg.SetOutputOn(functionGeneratorChannelInFocus);
            PlayWaveform.Content = "Restart Waveform";  // when the play waveform button is clicked again it restarts the waveform
            // from the beginning
        }

        private void Button_Click_EStop(object sender, RoutedEventArgs e)
        {
            fg.SetAllOutputsOff();  // turn off all outputs
            channelsPlaying.Clear();
            calibration = false;
            PlayWaveform.Content = "Play Waveform";
            channelsPlaying.Remove(functionGeneratorChannelInFocus);
            if (calibration)
            {
                PlayWaveform.IsEnabled = true;
                CalibrationButton.IsEnabled = true;
                WaveformList.IsEnabled = true;
                calibration = false;
                fg.SetWaveformType(WaveformType.ARB, functionGeneratorChannelInFocus);  // after the user stops calibration switch back to ARB mode
            }
        }

        private void Button_Click_StopWaveform(object sender, RoutedEventArgs e)
        {
            fg.SetOutputOff(functionGeneratorChannelInFocus);  // turn off the current channel's output
            channelsPlaying.Remove(functionGeneratorChannelInFocus);
            PlayWaveform.Content = "Play Waveform";
            if (calibration)
            {
                CalibrationButton.IsEnabled = true;
                WaveformList.IsEnabled = true;
                calibration = false;
                fg.SetWaveformType(WaveformType.ARB, functionGeneratorChannelInFocus);  // after the user stops calibration switch back to ARB mode
            }
        }

        private void ParseFileHandler(string filePath)
        {
            var t = new Thread(() => ParseFile(filePath));  // spin off a new thread for file parsing
            t.Start();
        }

        private void ParseFile(string filePath)
        {

            double sampleRate = 844;  // default samplerate
            string fileName = System.IO.Path.GetFileName(filePath);
            var fileLines = File.ReadLines(filePath);
            if (fileLines.First().StartsWith("samplerate="))
            {
                sampleRate = double.Parse(fileLines.First().Substring(11));  // parse the sampleRate
                fileLines = fileLines.Skip(1);  // and then we have to skip the first one.
            }
            double[] voltageArray = fileLines.AsParallel().AsOrdered().Select(line => double.Parse(line)).ToArray();
            // parse to an IEnumerable of doubles, using Parallel processing, but preserving the order
            if (voltageArray.AsParallel().Max() - voltageArray.AsParallel().Min() > maximumAllowedAmplitude)  // amplitude is too high
            {
                // signal the UI thread to display the error/warning
                Application.Current.Dispatcher.Invoke(() => {
                    AmplitudeErrorLabel.Visibility = Visibility.Visible;
                });
                double absMax = Math.Max(Math.Abs(voltageArray.AsParallel().Max()), Math.Abs(voltageArray.AsParallel().Min()));
                // get the maximum absolute value from the waveform.
                // now we can't just use the ScaleWaveform() function for this because then it would set the original 
                // values to ones that were beyond the max/min. 
                double scalingFactor = absMax / (maximumAllowedAmplitude / 2);  // get the scaling factor
                Parallel.For(0, voltageArray.Length, (i, state) =>
                {
                    voltageArray[i] = voltageArray[i] / scalingFactor;  // then we multiply every value in the waveform by the scaling factor.
                });
                // and then we just move on

            }
            int returnedValue = RemoveDCOffset(voltageArray);  // remove the DC offset from the waveform if there is one.
            if (returnedValue == -1)  // then there was an error removing the DC offset
            {
                // signal the UI thread to show the DC offset removal error and clean up the opening file process
                Application.Current.Dispatcher.Invoke(() => {
                    OffsetErrorLabel.Visibility = Visibility.Visible;
                    cancelFileOpen.Visibility = Visibility.Hidden;  // hide the cancel file open button
                    WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the save file instructions
                    WaveformList.IsEnabled = true; // also reenable the list
                });
                openingFile = false;  // set opening file to false
                return;  // then just give up without actually saving the waveform or anything.
            }

            // for safety reasons, we will only be putting in a maximum amplitude of 1Vpp into our bucket.
            // However, checking for this does make the code less flexible, so here are the lines of code that cause this.
            // 1Vpp checking code:
            // should we check this before or after DC offset removal. Before is faster, but after is better. So after.

            // okay so we need to automatically scale the amplitude down to fit the maximum allowed amplitude.



            WaveformFile fileStruct = new WaveformFile(sampleRate, voltageArray.Length, fileName, voltageArray, filePath);

            // create a new WaveformFileStruct to contain the info about this waveform file

            currentWaveform = fileStruct;  // then set the waveform data for the currently selected memory address
            Application.Current.Dispatcher.Invoke(DrawFGWaveformGraph);  // attempt to signal the UI thread to update the graph as soon
            // as we are done doing the parsing

        }

        /// <summary>
        /// This function removes the DC offset from the waveform given, by taking the average and subtracting it from all points
        /// </summary>
        /// <param name="voltageArray">The array of voltages</param>
        /// <returns>0 if the operation succeeded, -1 if there was an error</returns>
        private int RemoveDCOffset(double[] voltageArray)
        {
            double average = voltageArray.AsParallel().Sum() / voltageArray.Length; // get the average voltage of the waveform in the array.
            if (Math.Abs(average) < 0.0001)  // if the DC offset is really that small (less than 1mV)
                                             // then there's no need to worry about it
                                             // and possibly introduce artifacts into the waveform with floating point errors
            {
                return 0;
            }
            if ((voltageArray.AsParallel().Max() - average) > maximumAllowedAmplitude / 2)  // if after we remove the offset
                                                                                            // stuff would get truncated or crash the function generator, we have a problem.
            {
                return -1;  // DO something here to let the user know there was a problem
            }
            if ((voltageArray.AsParallel().Min() - average) < -1 * (maximumAllowedAmplitude / 2))  // if after we remove the offset
                                                                                                   // stuff would get truncated or crash the function generator, we have a problem.
            {
                return -1;  // do something here to let the user know there was a problem
            }
            // POSSIBLY ALERT USER ABOUT A DC OFFSET IN THE WAVEFORM
            // this parallel for thing is amazing
            Parallel.For(0, voltageArray.Length, (i, state) =>
            {

                voltageArray[i] -= average;  // we subtract the average from every voltage in the waveform, therefore removing the 
                                             // DC offset

            });
            return 0;
        }

        private void Button_Click_Calibrate(object sender, RoutedEventArgs e)
        {
            calibration = true;
            fg.CalibrateWaveform(functionGeneratorChannelInFocus);
            CalibrationButton.IsEnabled = false;
            WaveformList.IsEnabled = false;

        }

        // a double click on one of the memory locations in the list
        private void WaveformList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings            
            WaveformFile current = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
            OffsetErrorLabel.Visibility = Visibility.Hidden;  // hide the offset error label if it's showing
            AmplitudeErrorLabel.Visibility = Visibility.Hidden;  // hide the amplitude error one too
            if (openingFile)
            { // if we are currently in opening file mode
                if (current.FileName == null)  // if the memory location that was clicked on is empty, just save the data there.
                {
                    fileDataMap[memoryLocationSelected] = currentWaveform;  // save the data
                    openingFile = false;  // set opening file to false
                    WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the instruction label
                    cancelFileOpen.Visibility = Visibility.Hidden;  // hide the cancel button again.
                    WaveformName.Content = currentWaveform.FileName;  // show the displayed name
                    WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                    WaveformSampleRate.Text = currentWaveform.SampleRate.ToString();  // show the sample rate
                    WaveformAmplitudeScaleFactor.Text = currentWaveform.ScaleFactor.ToString();  // show the scaling factor
                    EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
                    ListBoxItem item = WaveformList.ItemContainerGenerator.ContainerFromItem(WaveformList.SelectedItem) as ListBoxItem;
                    item.Background = Brushes.Gold;
                    // set the color to gold to signal that the waveform is saved but
                    // not uploaded
                    if (!(uploading || loading))
                    {
                        UploadWaveform.IsEnabled = true;  // enable the upload button
                    }
                    DrawFGWaveformGraph();  // draw the graph

                    return;  // then just return.

                }
                else // if the memory location that was clicked on isn't empty, ask the user if they are okay with overwriting the data that
                     // is already there
                {
                    if (MessageBox.Show("Overwrite Waveform in " + memoryLocationSelected, "Overwrite Waveform?",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        fileDataMap[memoryLocationSelected] = currentWaveform; // they said it was okay to overwrite
                        openingFile = false;  // set opening file to false
                        cancelFileOpen.Visibility = Visibility.Hidden;  // hide the cancel button again.
                        WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the instruction label
                        WaveformName.Content = currentWaveform.FileName;  // show the displayed name
                        WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                        WaveformSampleRate.Text = currentWaveform.SampleRate.ToString();  // show the sample rate
                        WaveformAmplitudeScaleFactor.Text = currentWaveform.ScaleFactor.ToString();  // show the scaling factor
                        DrawFGWaveformGraph();  // draw the graph
                        if (!(uploading || loading))
                        {
                            UploadWaveform.IsEnabled = true;  // enable the upload button
                        }
                        ListBoxItem item = WaveformList.ItemContainerGenerator.ContainerFromItem(WaveformList.SelectedItem) as ListBoxItem;
                        EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
                        item.Background = Brushes.Gold;  // set the color to gold to signal that the waveform is saved but
                        // not uploaded
                        return;  // then just return.

                    }
                    else
                    {
                        // we don't set opening file to false because the user may have clicked on the wrong waveform or something
                        // the empty else branch is left here as a placeholder.
                        return;  // just return
                    }
                }
            }
            else  // if the user is not opening a file, and just double clicks on the memory location, load the waveform there into current
            {
                current = fileDataMap[memoryLocationSelected];  // reload current value
                                                                // if the waveform selected has set values, display them for the user.
                                                                // (Then also like load the file into the graph but that's for later)
                currentWaveform = current;
                if (current.FileName == null)  // if there isn't any data saved there, keep the original "no data" messages
                {
                    WaveformName.Content = "No data in requested location";
                    WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                    WaveformSampleRate.Text = "No data in requested location";
                    WaveformAmplitudeScaleFactor.Text = "No data in requested location";  // show the scaling factor
                    EditWaveformParameterCheckbox.IsEnabled = false;  // disable the edit waveform parameter checkbox
                    DrawFGWaveformGraph();  // draw an empty graph
                    UploadWaveform.IsEnabled = false;  // disable the upload button
                    return;
                }  // if it's not null, just show it's data
                WaveformName.Content = current.FileName;  // show the displayed name
                WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                WaveformSampleRate.Text = current.SampleRate.ToString();  // show the sample rate
                WaveformAmplitudeScaleFactor.Text = currentWaveform.ScaleFactor.ToString();  // show the scaling factor
                EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
                if (!(uploading || loading))
                {
                    UploadWaveform.IsEnabled = true;  // enable the upload button
                }
                DrawFGWaveformGraph();  // draw the graph with the data saved in the memory location
            }

        }

        // a single click on one of the memory locations in the list
        private void WaveformList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            OffsetErrorLabel.Visibility = Visibility.Hidden;  // hide the offset error label if it's showing
            AmplitudeErrorLabel.Visibility = Visibility.Hidden; // hide the amplitude one too
            memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings
            WaveformFile current = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location

            if (current.FileName == null)  // if the waveform selected still has its default values, just let the user know and then return
            {
                WaveformName.Content = "No data in requested location";
                WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                WaveformSampleRate.Text = "No data in requested location";
                WaveformAmplitudeScaleFactor.Text = "No data in requested location";  // show the scaling factor

                DrawFGWaveformGraph(current);  // draw an empty graph, with just the 0V reference line

                UploadWaveform.IsEnabled = false;  // try just disabling the button instead of hiding it
                EditWaveformParameterCheckbox.IsEnabled = false;  // disable the edit waveform parameter checkbox
                LoadWaveformButton.IsEnabled = false;  // if there's nothing in the memory location, we definitely can't load it
                PlayWaveform.IsEnabled = false;  // we definitely can't play it if there's nothing there
                return;
            }
            else
            {
                if (!openingFile)
                {
                    currentWaveform = current;
                }
                // UploadWaveform.IsEnabled = true;  // try just enabling the button instead of showing it
                EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
                if (currentWaveform.IsUploaded)  // if the waveform in the memory location is uploaded to the generator
                {
                    if (!(uploading || loading))  // as long as no operation is in progress
                    {
                        LoadWaveformButton.IsEnabled = true;  // we can enable the button to load the waveform into active memory
                        UploadWaveform.IsEnabled = true;
                    }
                    if (currentWaveform.ChannelsLoadedTo.Contains(functionGeneratorChannelInFocus))  // if the waveform is loaded into the 
                                                                                    //selected channel's active memory. 
                    {
                        if (!loading)  // if we're not currently loading a waveform
                        {
                            PlayWaveform.IsEnabled = true;  // enable the playwaveform button
                        }
                    }
                    else
                    {
                        PlayWaveform.IsEnabled = false;  // just in case
                    }
                }
                else
                {
                    if (!(uploading || loading))
                    {
                        UploadWaveform.IsEnabled = true;
                    }
                    LoadWaveformButton.IsEnabled = false;  // just in case
                    PlayWaveform.IsEnabled = false;
                }
                // if the waveform selected has set values, display them for the user.
                WaveformName.Content = current.FileName;  // show the displayed name
                WaveformMemoryLocation.Content = memoryLocationSelected;  // show the memory location
                WaveformSampleRate.Text = current.SampleRate.ToString();  // show the sample rate
                WaveformAmplitudeScaleFactor.Text = current.ScaleFactor.ToString();  // show the scaling factor
                DrawFGWaveformGraph(current);  // draw the waveform on the app's graph
            }
        }

        private void Button_Click_CancelFileOpen(object sender, RoutedEventArgs e)
        {
            openingFile = false;  // set file opening flag to false
            cancelFileOpen.Visibility = Visibility.Hidden;  // and then hide the button
            WaveformSaveInstructionLabel.Visibility = Visibility.Hidden;  // hide the instruction label
            DrawFGWaveformGraph(null);  // draw with a null WaveformFile to clear the graph after the user clicked cancel
        }

        private void ChannelChanged()
        {
            if (currentWaveform.ChannelsLoadedTo.Contains(functionGeneratorChannelInFocus))
                // if we switch the channel and the current waveform is loaded to the channel we switched to
            {
                if (!loading)
                {
                    PlayWaveform.IsEnabled = true; // enable the button to play the waveform
                }
            }
            else  // if the opposite happened, where the current waveform is not loaded to the channel we switched to, disable the playwaveform
                  // button
            {
                PlayWaveform.IsEnabled = false;
            }
        }

        private void UploadWaveform_Click(object sender, RoutedEventArgs e)
        {
            uploading = true;  // we can block other UI things from happening. i.e. switching to channel 2 and then back could in some
                               // cases re-activate disabled buttons, and then allow the user to do strange things.
            WaveformUploadMessage.Visibility = Visibility.Visible;  // show the "uploading waveform please wait" message
            // UPLOADING WILL BE DONE IN A SEPERATE THREAD
            WaveformFile data = currentWaveform;
            EditWaveformParameterCheckbox.IsEnabled = false;  // disable the edit waveform parameter checkbox
            currentWaveform.ChannelsLoadedTo.Clear();  // when a new waveform is uploaded, it won't be loaded to any channel
            PlayWaveform.IsEnabled = false;  // we know that this is false because we've cleared everything. This just saves us the trouble
                                             // of forcing an update.
            LoadWaveformButton.IsEnabled = false;
            // these are all non-channel specific, however should probably 
            // stop the user from loading a waveform while the waveform is uploading
            // that could result in the user loading the previous waveform stored in that location, and then overwriting it with the 
            // parameters of the new one, while keeping the waveform the same. This could result in a DC offset and H2 production.

            UploadWaveform.IsEnabled = false;  // disable the upload waveform button. If the user spam clicks it, then we have a problem
            double[] voltages = data.Voltages;
            double SampleRate = data.SampleRate;
            string memLocation = string.Copy((string)WaveformList.SelectedItem);
            // on button click, upload the waveform to the function generator
            //ThreadPool.QueueUserWorkItem(ThreadProc);
            ThreadPool.QueueUserWorkItem(lamdaThing => {
                try
                {  // again we can't use lowLevel and highLevel here because of the way that amplitude scaling works.
                    // if all this parallelism causes CPU bottleneck I could store the scaled min and max as well, or just
                    // have it multiply the original high/low levels by the scaling factor.
                    // actually that's not a bad plan.
                    fg.UploadWaveformData(voltages, SampleRate, (data.ScaleFactor * (data.HighLevel + data.LowLevel)) / 2, 0, memLocation);
                }
                finally
                {
                    WaveformUploadedCallback();
                }
            });
            ListBoxItem item = WaveformList.ItemContainerGenerator.ContainerFromItem(WaveformList.SelectedItem) as ListBoxItem;
            item.Background = Brushes.Green;  // set the background so the user knows that the waveform has been uploaded
            currentWaveform.IsUploaded = true;  // stuff uses this flag
        }

        private void WaveformUploadedCallback()
        {
            // signal the UI thread to update the following UI elements
            Application.Current.Dispatcher.Invoke(() => {
                memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings    
                WaveformFile temp = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
                if (temp.IsUploaded)  // this stops the load button from enabling if the user switched waveforms during the upload process
                {
                    LoadWaveformButton.IsEnabled = true;  // only once the uploading is complete do 
                }
                // we re-enable the button to load the waveform
                UploadWaveform.IsEnabled = true;  // and the button to upload another one
                WaveformUploadMessage.Visibility = Visibility.Hidden;  // hide the "uploading waveform please wait" message
                EditWaveformParameterCheckbox.IsEnabled = true;  // enable the edit waveform parameter checkbox
            });
            uploading = false;  // turn off the uploading flag
        }

        private void CheckMemoryDepth(int[] tempAllowedMemDepths)
        {
            int currentMemDepth = Array.IndexOf(tempAllowedMemDepths, scope.GetMemDepth());
            if (currentMemDepth >= 0)
            {
                MemoryDepthComboBox.SelectedIndex = currentMemDepth + 1;  // AUTO is in index, so we need to increment by 1.
            }
            else
            {
                MemoryDepthComboBox.SelectedIndex = 0;  // AUTO
            }
        }

        private void LoadWaveformButton_Click(object sender, RoutedEventArgs e)
        {
            //loading = true;  // enable the loading flag
            PlayWaveform.IsEnabled = false;  // gray out the play button until the Loading is complete.
            LoadWaveformButton.IsEnabled = false;  // gray out the load button until this is done, if the user spam clicks it,
            UploadWaveform.IsEnabled = false;  // disable the upload waveform button while loading a waveform
                                               // there will likely be issues.
                                               // if the user switches channels to play a waveform that is 
            string memLocation = string.Copy((string)WaveformList.SelectedItem);
            WaveformLoadMessage.Visibility = Visibility.Visible;  // hide the "waveform loading please wait" message
            ThreadPool.QueueUserWorkItem(lamdaThing => {
                try
                {
                    fg.LoadWaveform(memLocation, functionGeneratorChannelInFocus);
                }
                finally
                {
                    // it seems like the function generator signals operation completion before it's actually done.
                    // how do we do anything about that though?
                    LoadWaveformCallback();  // when the loading is complete actually activate the callback function to
                                             // enable the buttons. This is because loading can take like 30 seconds at worst, and clicking play
                                             // while one is loading is bad
                }
            });
            // load the waveform using a new thread. This is very very very important for large waveforms as they will block the
            // thread for like 30 sec at maximum.
        }

        private void LoadWaveformCallback()
        {
            loading = false;  // disable the loading flag
            currentWaveform.ChannelsLoadedTo.Add(functionGeneratorChannelInFocus);  // add the current channel to the current waveform's set
                                                                   // of channels it's loaded to
                                                                   // signal the UI thread to update the state of the elements
            Application.Current.Dispatcher.Invoke(() =>
            {
                memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings    
                WaveformFile temp = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
                if (temp.IsUploaded)
                {
                    LoadWaveformButton.IsEnabled = true;  // we ungray out the load button once complete
                }
                if (temp.ChannelsLoadedTo.Contains(functionGeneratorChannelInFocus))
                {
                    PlayWaveform.IsEnabled = true;  // The loading is complete, so we ungray out the button
                }
                if (temp.FileName != null)
                {
                    UploadWaveform.IsEnabled = true;  // re-enable the upload button
                }
                WaveformLoadMessage.Visibility = Visibility.Hidden;  // hide the "waveform loading please wait" message
            });

        }

        private void PreviewSampleRateInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextAllowed(e.Text);
        }

        private static readonly Regex _regex = new Regex("[^0-9.]+"); //regex that matches disallowed text
        private static bool IsTextAllowed(string text)
        {
            return !_regex.IsMatch(text);
        }

        private void SaveWaveformParameters_Click(object sender, RoutedEventArgs e)
        {
            memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings            
            WaveformFile current = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
            double sampleRate = double.Parse(WaveformSampleRate.Text);
            if (sampleRate == 0)  // don't let the user set the sample rate to 0 Hz.
            {
                WaveformSampleRate.Text = current.SampleRate.ToString();  // just have it set back to what is was before if they try to put in 0
            }
            else
            {
                current.SampleRate = sampleRate;  // else set the sample rate to what they put in
            }
            double scaleFactor = double.Parse(WaveformAmplitudeScaleFactor.Text);  // get the user set scale factor by parsing the textbox text
            if (scaleFactor != current.ScaleFactor)  // if the scalefactor parsed from the text box doesn't match the one in the WaveformFile
            {
                // then the user has changed the scale factor
                ScaleAmplitude(current, scaleFactor);
                current.ScaleFactor = scaleFactor;  // and then update the scale factor saved in the WaveformFile
            }
            current.IsUploaded = false;
            ListBoxItem item = WaveformList.ItemContainerGenerator.ContainerFromItem(WaveformList.SelectedItem) as ListBoxItem;
            item.Background = Brushes.Gold;  // set the color back to gold to signal that the waveform is saved but is not uploaded.
            // the user will have to click upload again to see changes.
        }

        private void ScaleAmplitude(WaveformFile fileToScale, double scaleFactor)
        {
            double[] originalVoltages = fileToScale.OriginalVoltages;  // get the unscaled voltage array

            if (scaleFactor.Equals(1))  // for sanity, using the .Equals() method
            {
                fileToScale.Voltages = fileToScale.OriginalVoltages;  // set the voltage reference in the WaveformFile to be the original
                // voltages if the user sets the scale factor to 1.
                DrawFGWaveformGraph();  // draw the graph, can't forget that
                ScalingErrorLabel.Visibility = Visibility.Hidden;
                return;  // then we're done.
            }
            // better to do this checking before we start processing rather than having the API throw an error after we've done all of the work.
            if (fileToScale.HighLevel * scaleFactor > maximumAllowedAmplitude / 2)
            {

                ScalingErrorLabel.Visibility = Visibility.Visible;  // show the scaling error label
                return;
            }
            if (fileToScale.LowLevel * scaleFactor < -1 * (maximumAllowedAmplitude / 2))
            {
                // DO UI SIGNALING THAT SOMETHING IS WRONG
                ScalingErrorLabel.Visibility = Visibility.Visible;  // show the scaling error labelG
                return;
            }
            double[] scaledVoltages = fileToScale.ScaledVoltages;  // get a reference to the scaled voltages in the WaveformFile
            ScalingErrorLabel.Visibility = Visibility.Hidden;
            if (scaledVoltages == null)  // we only init the scaled voltages array when needed.
            {
                // dealing with the shifting around of references to counteract stuff is easier than trying to get the array inside that
                // WaveformFile to be passed by reference of reference.
                scaledVoltages = new double[originalVoltages.Length];  // it will (and must) always be 
                // the same length as the original Voltage array
                fileToScale.ScaledVoltages = scaledVoltages;  // reference weirdness, pay no mind
            }
            Parallel.For(0, originalVoltages.Length, (i, state) => {
                scaledVoltages[i] = originalVoltages[i] * scaleFactor;  // using a parallel for loop, iterate through each of the voltages and 
                                                                        // multiply it by the scale factor before placing it in the scaled voltage array.
            });
            RemoveDCOffset(scaledVoltages);  // remove any DC offset this process created.
            fileToScale.Voltages = scaledVoltages;  // change the reference to scaled voltages
            DrawFGWaveformGraph();  // and then we redraw the graph so that the changes to the waveform show up immediately
        }


        private void EditWaveformParameterCheckbox_Checked(object sender, RoutedEventArgs e)
        {
            WaveformSampleRate.IsReadOnly = false;
            WaveformSampleRate.IsEnabled = true;
            SaveWaveformParameters.IsEnabled = true;  // enable the save waveform parameters button
            WaveformAmplitudeScaleFactor.IsReadOnly = false;   // make the scale factor textbox not read only
            WaveformAmplitudeScaleFactor.IsEnabled = true;
        }

        private void EditWaveformParameterCheckbox_UnChecked(object sender, RoutedEventArgs e)
        {
            WaveformSampleRate.IsReadOnly = true;
            SaveWaveformParameters.IsEnabled = false;  // disable the save waveform parameters button
            WaveformSampleRate.IsEnabled = false;  // disable the input
            WaveformAmplitudeScaleFactor.IsEnabled = false;
            WaveformAmplitudeScaleFactor.IsReadOnly = true;   // make the scale factor textbox read only 
            memoryLocationSelected = (string)WaveformList.SelectedItem;  // the data stored in WaveformList will always be strings    
            WaveformFile temp = fileDataMap[memoryLocationSelected];  // get the waveformFileStruct from the selected memory location
            WaveformSampleRate.Text = temp.SampleRate.ToString();
            WaveformAmplitudeScaleFactor.Text = temp.ScaleFactor.ToString();
            if (ScalingErrorLabel.Visibility == Visibility.Visible)  // if there was an error when the user unchecks the box
            {
                WaveformAmplitudeScaleFactor.Text = "1";  // just set everything back to 1
                temp.ScaleFactor = 1;
            }
            ScalingErrorLabel.Visibility = Visibility.Hidden;  // then hide the error

        }

        private void WaveformScaleFactor_TextChanged(object sender, TextChangedEventArgs e)
        {
            // currently do nothing on this event
        }

        private void WaveformSampleRate_TextChanged(object sender, TextChangedEventArgs e)
        {
            // currently do nothing on this event
        }

        class WaveformFile
        {
            public double LowLevel { get; set; }
            public double HighLevel { get; set; }
            public double SampleRate { get; set; }
            public int NumSamples { get; set; }
            public string FileName { get; set; }
            public double[] Voltages { get; set; }
            public double[] OriginalVoltages { get; }
            public double[] ScaledVoltages { get; set; }
            public string FilePath { get; set; }
            public bool IsUploaded { get; set; }
            public double ScaleFactor { get; set; }

            public HashSet<int> ChannelsLoadedTo;  // the set of the channel numbers that this waveform is loaded to

            public WaveformFile(double sampleRate, int numSamples, string fileName, double[] voltages, string filePath)
            {
                SampleRate = sampleRate;
                NumSamples = numSamples;
                FileName = fileName;
                OriginalVoltages = voltages;
                Voltages = OriginalVoltages;  // set the reference of voltages to the original voltages passed in (this is for scaling stuff)
                                              // original voltages is also not mutable. we can't set it.
                ScaledVoltages = null;  // this starts off null so we don't need to allocate another (up to 8million) double array in memory
                                        // unless we need to.
                                        // Example of the Lazy Initialization design pattern. Shoutout to UW CSE 331 and Dr. Hal Perkins.
                FilePath = filePath;
                ScaleFactor = 1;  // On construction of a new WaveformFile object, the scaling factor of the waveform in it is set at 1.
                ChannelsLoadedTo = new HashSet<int>();  // a set that contains the numbers of each channel that this waveform is loaded to
                IsUploaded = false;  // on creation of new struct, IsUploaded is always false

                // If I can get this to work well it would be nice because it would let us make this UI more flexible, and able to work
                // with function generators with varying numbers of channels, not just two.
                if (voltages == null)  // bit of a hack.
                {
                    LowLevel = 0;
                    HighLevel = 0;
                }
                else
                {
                    LowLevel = voltages.AsParallel().Min();
                    HighLevel = voltages.AsParallel().Max();
                }

            }

            public WaveformFile() : this(0, 0, null, null, null)
            {
                // constructor chaining in C# is weird.
            }
        }


        // OSCILLOSCOPE STUFF

        private void SetRefreshTimer()  // enable the refresh timer which every (interval) ms, grabs new wave data and updates the graph
        {
            refreshTimer = new System.Timers.Timer(refreshInterval);  // init the timer
            // Hook up the Elapsed event for the timer. 
            refreshTimer.Elapsed += UpdateEvent;  // add the drawgraph event
            refreshTimer.AutoReset = true;  // make it keep repeating
            refreshTimer.Enabled = true;  // enable it

        }

        private void UpdateEvent(object o, ElapsedEventArgs e)  // gotta have two functions here so this one can have the required
                                                                // object o, ElapsedEventArgs e params
        {
            if (drawGraph)  // might be too much but hey, this was a crazy thing to fix so let's just not touch this
            {
                lock (downloadLock)
                {
                    DrawGraphHandler(channelsToDraw);
                }
            }
        }
        private void DrawGraphHandler(HashSet<int> channelsToDraw)
        {
            foreach (int channel in channelsToDraw)
            {

                // spin graph drawing off into seperate thread
                ThreadPool.QueueUserWorkItem(state => DrawGraph(channel, channelColors[channel-1]));
            }
        }

        private void DrawGraph(int channelParam, System.Drawing.Color color)
        {

            lock (downloadLock)  // for when we're downloading deep memory waveforms
            {
                double[] waveData;
                double currentScale;
                double triggerLevel;
                double voltageOffset;
                lock (graphLock)  // for when we're trying to graph multiple channels at a time
                {
                    waveData = scope.GetWaveVoltages(channelParam);  // grab the point data from the scope for the given channel
                    currentScale = scope.GetYScale();  // get the voltage scale
                    triggerLevel = scope.GetTriggerLevel();
                    voltageOffset = scope.GetVerticalOffset(channelParam);
                }
                if (Application.Current == null)  // avoid weird errors on application close
                {
                    return;
                }
                Application.Current.Dispatcher.Invoke(() =>
                {

                    PointsRead.Content = waveData.Length;

                });

                double[] screenPositionArray = waveData.Select(dataPoint => ScaleVoltage(dataPoint, currentScale, voltageOffset)).ToArray();

                LineSeries temp = new LineSeries
                {
                    Color = OxyColor.FromArgb(color.A, color.R, color.G, color.B),  // used to associate the line with the respectively colored one on the scope
                                                                                    // oxyplots uses its own color handling so this is the result of that.
                };
                temp.YAxisKey = voltageAxes[channelParam - 1].Key;  // keep the axis aligned
                for (int i = 0; i < screenPositionArray.Length; i++)
                {

                    temp.Points.Add(new DataPoint(i - screenPositionArray.Length / 2, screenPositionArray[i]));
                    // subtract half the length so we can center the axes in the middle just like on the scope
                }
                if (Application.Current == null)
                {
                    return;
                }
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }
                Application.Current.Dispatcher.BeginInvoke(new Action<MainWindow>((sender) =>
                {
                    WaveformPlot.Model.Series.Remove(triggerLine);
                    WaveformPlot.Model.Series.Remove(channelGraphs[channelParam - 1]);  // remove the earlier graph from the waveform plot
                    channelGraphs[channelParam - 1] = temp;  // swap it with the new one
                    if (!channelsToDisable.Contains(channelParam)) // then if it's not supposed to be removed
                    {
                        WaveformPlot.Model.Series.Add(channelGraphs[channelParam - 1]);  // just swap it with the new one
                    }
                    Axis channelAxis = WaveformPlot.Model.Axes.First(item => item.Key == channelParam.ToString());
                    channelAxis.Minimum = (-1 * (currentScale / 2));
                    channelAxis.Maximum = (currentScale / 2);
                    channelAxis.IsZoomEnabled = false;
                    channelAxis.IsPanEnabled = false;
                    if (showTriggerLine)  // only show the trigger line on the graph if showTriggerLine is enabled
                    {
                        triggerLine = new LineSeries()
                        {
                            Dashes = new double[] { 2, 3 },   // to make the dashed line match the one on the scope as best as possible.
                            Color = OxyColor.FromRgb(255, 165, 0),  // orange
                        };
                        double triggerLineScaled = (triggerLevel + ((voltageOffsetScaleConstant / 2) * currentScale)) / (voltageOffsetScaleConstant * currentScale);
                        // minus -4 * currentScale for DS1054z
                        double triggerLineScreenValue = (triggerLineScaled * currentScale) - (currentScale / 2);
                        triggerLine.Points.Add(new DataPoint((-1 * screenPositionArray.Length / 2), triggerLineScreenValue));
                        triggerLine.Points.Add(new DataPoint(screenPositionArray.Length / 2, triggerLineScreenValue));  // magic number -600 and 600 consider revising
                        WaveformPlot.Model.Series.Add(triggerLine);
                    }
                    WaveformPlot.Model.InvalidatePlot(true);
                    (OscilloscopeChannelScaleStackPanel.Children[channelParam - 1] as Label).Content = channelParam + "=" + currentScale + "V";  // update scale label

                }), this);
            }
        }

        private double ScaleVoltage(double voltagePoint, double scale, double voltageOffset)
        {
            double fractionalScaled = (voltageOffset + voltagePoint + ((voltageOffsetScaleConstant / 2) * scale)) / (voltageOffsetScaleConstant * scale);
            double screenValue = (fractionalScaled * scale) - (scale / 2);  // multiply the fractional value by the voltage scale from the scope
            return screenValue;
        }

        private void RunButton_Click(object sender, RoutedEventArgs e)
        {
            scope.Run();
            RunLabel.Content = "Running";
            MemoryDepthComboBox.IsEnabled = true;  // allow the memory depth to be changed again
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            RunLabel.Content = "Stopped";
            scope.Stop();
            MemoryDepthComboBox.IsEnabled = false;  // when the scope is stopped, the memory depth cannot be changed
        }

        private void VoltageScalePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

            int index = VoltageScalePresetComboBox.SelectedIndex;
            scope.SetYScale(scopeChannelInFocus, mappedVoltageScales[index]);
            VoltageOffsetSlider.Maximum = voltageOffsetScaleConstant * mappedVoltageScales[index];
            VoltageOffsetSlider.Minimum = -1 * VoltageOffsetSlider.Maximum;
            TriggerSlider.Maximum = triggerPositionScaleConstant * mappedVoltageScales[index];
            TriggerSlider.Minimum = -1 * TriggerSlider.Maximum;
            VoltageOffsetSlider.Value *= (mappedVoltageScales[index] / previousYScaleFactor);
            TriggerSlider.Value *= (mappedVoltageScales[index] / previousYScaleFactor);  // scale the current value of the voltage offset
                                                                                         // and trigger so that they stay in the same relative position, even if the scale has changed.
            VoltageOffsetValue.Content = VoltageOffsetSlider.Value;
            TriggerVoltageValue.Content = TriggerSlider.Value;  // update the numerical displays for the sliders as well
            previousYScaleFactor = scope.GetYScale(scopeChannelInFocus);
        }

        private void TimeScalePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = TimeScalePresetComboBox.SelectedIndex;
            scope.SetTimeScale(scopeChannelInFocus, mappedTimeScales[index]);
            PositionOffsetSlider.Maximum = timeOffsetScaleConstant * mappedTimeScales[index];
            PositionOffsetSlider.Minimum = -1 * PositionOffsetSlider.Maximum;
        }

        private void VoltageOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            VoltageOffsetValue.Content = VoltageOffsetSlider.Value;
            scope.SetVerticalOffset(scopeChannelInFocus, VoltageOffsetSlider.Value);
        }

        private void PositionOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PositionOffsetValue.Content = PositionOffsetSlider.Value;
            scope.SetPositionOffset(scopeChannelInFocus, PositionOffsetSlider.Value);
        }

        private void ZeroVoltageOffset_Click(object sender, RoutedEventArgs e)
        {
            VoltageOffsetValue.Content = 0;
            VoltageOffsetSlider.Value = 0;
            scope.SetVerticalOffset(scopeChannelInFocus, VoltageOffsetSlider.Value);
        }

        private void ZeroPositionOffset_Click(object sender, RoutedEventArgs e)
        {
            PositionOffsetValue.Content = 0;
            PositionOffsetSlider.Value = 0;
            scope.SetPositionOffset(scopeChannelInFocus, PositionOffsetSlider.Value);
        }

        private void TriggerCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            showTriggerLine = true;
        }

        private void TriggerCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            WaveformPlot.Model.Series.Remove(triggerLine);
            WaveformPlot.Model.InvalidatePlot(true);
            showTriggerLine = false;
        }

        private void TriggerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TriggerVoltageValue.Content = TriggerSlider.Value;  // update the numerical display of the trigger voltage value
            scope.SetTriggerLevel(TriggerSlider.Value);  // set the trigger level of the scope
        }

        private void ZeroTriggerVoltage_Click(object sender, RoutedEventArgs e)
        {
            scope.SetTriggerLevel(0);  // zero 
            TriggerVoltageValue.Content = 0;
            TriggerSlider.Value = 0;
        }

        private void MemoryDepthComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

            if (MemoryDepthComboBox.SelectedItem == null)
            {
                return;
            }
            if ((MemoryDepthComboBox.SelectedItem as string) == "AUTO")
            {
                scope.SetMemDepth(0);  // set to zero for auto memdepth per API documentation
                SaveWaveformCaptureButton.IsEnabled = false;  // we cannot do waveform captures when the scope is set to AUTO memdepth
                return;
            }
            scope.SetMemDepth((int)MemoryDepthComboBox.SelectedItem);
            SaveWaveformCaptureButton.IsEnabled = true;  // if the scope memdepth is not AUTO, we can do deep mem captures.
        }

        private void TriggerSingleButton_Click(object sender, RoutedEventArgs e)
        {
            scope.Single();
            RunLabel.Content = "Unknown";  // we currently don't have any way to tell if the scope is triggered other than polling a value over and over again.
            // if this ends up being important than I'll have to look into that.
        }

        private void SaveWaveformCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            drawGraph = false;
            DisableGraphAndUIElements();  // disable all the UI elements so the user can't mess with the capture
            scope.Stop();  // first stop the scope
            RunLabel.Content = "Stopped";
            scope.SetActiveChannel(scopeChannelInFocus);  // determine which channel we are capturing (only one at a time)
            int memDepth = scope.GetMemDepth();  // retrieve the memory depth of the scope
            ThreadPool.QueueUserWorkItem(lamda =>
            {
                lock (downloadLock)
                {
                    double currentScale = scope.GetYScale();
                    double YOrigin = scope.GetYOrigin();
                    double Yinc = scope.GetYIncrement();
                    double Yref = scope.GetYReference();
                    double xInc = scope.GetXIncrement();
                    double[] voltages = scope.GetDeepMemVoltages(scopeChannelInFocus);
                    //if (data.Length > memDepth)  // idk why this happens
                    //{
                    //    IEnumerable<byte> temp = data.Skip(data.Length - memDepth);  // we skip corrupted values at the beginning of the capture if there are any
                    //    data = temp.ToArray();  // this problem has likely been resolved.
                    //}
                    string directoryPath = Directory.GetCurrentDirectory() + "\\captures";
                    Directory.CreateDirectory(directoryPath);  // if the captures directory doesn't exist
                                                               // create it as a subdirectory of whatever current directory the program is located in
                    string timeStamp = string.Format("{0:yyyy-MM-dd_hh-mm-ss-fff}", DateTime.Now);  // get the current timestamp
                    currentLogFile = TextWriter.Synchronized(new StreamWriter(File.Create(directoryPath + "\\capture_" + timeStamp + ".csv")));
                    // create a timestamped log file
                    currentLogFile.WriteLine("channel,voltage,timestamp");  // write the header of the CSV file

                    for (int i = 0; i < voltages.Length; i++)  // this might be better suited for parallelism
                    {
                        // we write out the voltages to the file along with a relative timestamp for each calculated from the x increment
                        currentLogFile.WriteLine(scopeChannelInFocus + "," + voltages[i] + "," + (xInc * i));
                    }
                    currentLogFile.Flush();  // make sure stuff actually gets written out, even before the program is closed
                    currentLogFile.Close();
                    EnableGraphAndUIElements();
                }
            });


        }


        private void DisableGraphAndUIElements()
        {
            // make these able to be run from any thread without issue by putting this here
            Application.Current.Dispatcher.Invoke(() =>
            {
                drawGraph = false;
                SavingWaveformCaptureLabel.Visibility = Visibility.Visible; // show the "saving waveform please wait" label
                RunButton.IsEnabled = false;  // disable the run button until we're done here
                StopButton.IsEnabled = false;
                ZeroPositionOffset.IsEnabled = false;
                VoltageScalePresetComboBox.IsEnabled = false;
                TriggerSingleButton.IsEnabled = false;
                ZeroTriggerVoltage.IsEnabled = false;
                ZeroVoltageOffset.IsEnabled = false;
                MemoryDepthComboBox.IsEnabled = false;  // definitely disable this until we're done. We don't want the user changing the memory depth
                                                        // mid download (not like the scope would let them but still).
                TimeScalePresetComboBox.IsEnabled = false;
                PositionOffsetSlider.IsEnabled = false;
                VoltageOffsetSlider.IsEnabled = false;  // disable everything
                TriggerCheckBox.IsEnabled = false;
                foreach (CheckBox cb in OscilloscopeChannelButtonStackPanel.Children)
                {
                    cb.IsEnabled = false;
                }
                foreach (RadioButton rb in OscilloscopeRadioButtonStackPanel.Children)
                {
                    rb.IsEnabled = false;
                }

            });
        }

        private void EnableGraphAndUIElements()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                SavingWaveformCaptureLabel.Visibility = Visibility.Hidden; // hide the "saving waveform please wait" label
                RunButton.IsEnabled = true;  // disable the run button until we're done here
                StopButton.IsEnabled = true;
                MemoryDepthComboBox.IsEnabled = true;  // definitely disable this until we're done. We don't want the user changing the memory depth
                                                       // mid download (not like the scope would let them but still).
                TimeScalePresetComboBox.IsEnabled = true;
                VoltageScalePresetComboBox.IsEnabled = true;
                TriggerSingleButton.IsEnabled = true;
                ZeroPositionOffset.IsEnabled = true;
                ZeroTriggerVoltage.IsEnabled = true;
                ZeroVoltageOffset.IsEnabled = true;
                PositionOffsetSlider.IsEnabled = true;
                TriggerCheckBox.IsEnabled = true;
                VoltageOffsetSlider.IsEnabled = true;  // disable everything
                foreach (CheckBox cb in OscilloscopeChannelButtonStackPanel.Children)
                {
                    cb.IsEnabled = true;
                }
                foreach (RadioButton rb in OscilloscopeRadioButtonStackPanel.Children)
                {
                    rb.IsEnabled = true;
                }
            });
            drawGraph = true;
        }
    }
}
