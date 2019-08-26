using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using NationalInstruments.Visa;
using OscilloscopeAPI;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.IO;
using System.Threading;
using System.Timers;
using TestSuiteInterface;

namespace UnifiedTestSuiteApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int refreshInterval = 100;  // the graph refresh interval in ms
        private static System.Timers.Timer refreshTimer;  // gotta make sure there's no ambiguity with the threading timer
        private static IOscilloscope scope;
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
        private IFunctionGenerator fg;
        private bool calibration;
        private int functionGeneratorChannelInFocus;
        private bool openingFile;  // true if the user is opening a file
        private string memoryLocationSelected;
        private readonly HashSet<int> channelsPlaying;
        private double maximumAllowedAmplitude = 1;  // set to -1 to allow amplitudes up to the function generator's maximum
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
            IEnumerable<string> resources;
            try
            {
                resources = rm.Find("USB?*");  // find USB devices
                foreach (string s in resources)
                {
                    Console.WriteLine(s);  // write out each VISA device found.
                    // currently this program only works with the first one, and it better be an oscope.
                }
                scope = new DS1054Z(resources.FirstOrDefault(), rm);  // init the DS1054Z
                // gotta think about autoscaling on startup. More research on how well the scope's autoset function performs is required

            }
            catch (Exception ex)  // if no devices are found, throw an error and quit
            {
                Console.WriteLine(ex.Message);
                return;
            }
            scope.Run();  // start the scope
            scopeChannelInFocus = 1;  // start with channel 1 in focus
            InitializeComponent();
            InitializeComponent();  // ALL THINGS THAT ACTUALLY CHANGE UI PROPERTIES HAVE TO GO UNDER THIS
            WaveformLoadMessage.Visibility = Visibility.Hidden;  // hide the "waveform loading please wait" message
            WaveformUploadMessage.Visibility = Visibility.Hidden;  // hide the "uploading waveform please wait" message
            waveformGraph.Background = Brushes.Black;  // set the canvas background to black
                                                       // ch1.IsChecked = true;  // start with channel 1 being checked, and therefore the default channel used

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
    }
}
