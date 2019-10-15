using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Timers;
using System.Windows;
using System.Windows.Controls;

namespace UnifiedTestSuiteApp
{
    public partial class MainWindow : Window
    {
        private void OScope_CheckMemoryDepth(int[] tempAllowedMemDepths)
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


        private void OScope_SetRefreshTimer()  // enable the refresh timer which every (interval) ms, grabs new wave data and updates the graph
        {
            refreshTimer = new System.Timers.Timer(refreshInterval);  // init the timer
            // Hook up the Elapsed event for the timer. 
            refreshTimer.Elapsed += OScope_UpdateEvent;  // add the drawgraph event
            refreshTimer.AutoReset = true;  // make it keep repeating
            refreshTimer.Enabled = true;  // enable it

        }

        private void OScope_UpdateEvent(object o, ElapsedEventArgs e)  // gotta have two functions here so this one can have the required
                                                                       // object o, ElapsedEventArgs e params
        {
            if (drawGraph)  // might be too much but hey, this was a crazy thing to fix so let's just not touch this
            {
                lock (downloadLock)
                {
                    OScope_DrawGraphHandler(channelsToDraw);
                }
            }
        }
        private void OScope_DrawGraphHandler(HashSet<int> channelsToDraw)
        {
            foreach (int channel in channelsToDraw)
            {

                // spin graph drawing off into seperate thread
                ThreadPool.QueueUserWorkItem(state => OScope_DrawGraph(channel, channelColors[channel - 1]));
            }
        }

        private void OScope_DrawGraph(int channelParam, System.Drawing.Color color)
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
                    voltageOffset = scope.GetYAxisOffset(channelParam);
                }
                if (Application.Current == null)  // avoid weird errors on application close
                {
                    return;
                }
                Application.Current.Dispatcher.Invoke(() =>
                {

                    PointsRead.Content = waveData.Length;

                });

                double[] screenPositionArray = waveData.Select(dataPoint => OScope_ScaleVoltage(dataPoint, currentScale, voltageOffset)).ToArray();

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

        private double OScope_ScaleVoltage(double voltagePoint, double scale, double voltageOffset)
        {
            double fractionalScaled = (voltageOffset + voltagePoint + ((voltageOffsetScaleConstant / 2) * scale)) / (voltageOffsetScaleConstant * scale);
            double screenValue = (fractionalScaled * scale) - (scale / 2);  // multiply the fractional value by the voltage scale from the scope
            return screenValue;
        }

        private void OScope_RunButton_Click(object sender, RoutedEventArgs e)
        {
            scope.Run();
            RunLabel.Content = "Running";
            MemoryDepthComboBox.IsEnabled = true;  // allow the memory depth to be changed again
        }

        private void OScope_StopButton_Click(object sender, RoutedEventArgs e)
        {
            RunLabel.Content = "Stopped";
            scope.Stop();
            MemoryDepthComboBox.IsEnabled = false;  // when the scope is stopped, the memory depth cannot be changed
        }

        private void OScope_VoltageScalePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        private void OScope_TimeScalePresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = TimeScalePresetComboBox.SelectedIndex;
            scope.SetXAxisScale(mappedTimeScales[index]);
            PositionOffsetSlider.Maximum = timeOffsetScaleConstant * mappedTimeScales[index];
            PositionOffsetSlider.Minimum = -1 * PositionOffsetSlider.Maximum;
        }

        private void OScope_VoltageOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            VoltageOffsetValue.Content = VoltageOffsetSlider.Value;
            scope.SetYAxisOffset(scopeChannelInFocus, VoltageOffsetSlider.Value);
        }

        private void OScope_PositionOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PositionOffsetValue.Content = PositionOffsetSlider.Value;
            scope.SetXAxisOffset(PositionOffsetSlider.Value);
        }

        private void OScope_ZeroVoltageOffset_Click(object sender, RoutedEventArgs e)
        {
            VoltageOffsetValue.Content = 0;
            VoltageOffsetSlider.Value = 0;
            scope.SetYAxisOffset(scopeChannelInFocus, VoltageOffsetSlider.Value);
        }

        private void OScope_ZeroPositionOffset_Click(object sender, RoutedEventArgs e)
        {
            PositionOffsetValue.Content = 0;
            PositionOffsetSlider.Value = 0;
            scope.SetXAxisOffset(PositionOffsetSlider.Value);
        }

        private void OScope_TriggerCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            showTriggerLine = true;
        }

        private void OScope_TriggerCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            WaveformPlot.Model.Series.Remove(triggerLine);
            WaveformPlot.Model.InvalidatePlot(true);
            showTriggerLine = false;
        }

        private void Oscope_TriggerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TriggerVoltageValue.Content = TriggerSlider.Value;  // update the numerical display of the trigger voltage value
            scope.SetTriggerLevel(TriggerSlider.Value);  // set the trigger level of the scope
        }

        private void OScope_ZeroTriggerVoltage_Click(object sender, RoutedEventArgs e)
        {
            scope.SetTriggerLevel(0);  // zero 
            TriggerVoltageValue.Content = 0;
            TriggerSlider.Value = 0;
        }

        private void OScope_MemoryDepthComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        private void OScope_TriggerSingleButton_Click(object sender, RoutedEventArgs e)
        {
            scope.Single();
            RunLabel.Content = "Unknown";  // we currently don't have any way to tell if the scope is triggered other than polling a value over and over again.
            // if this ends up being important than I'll have to look into that.
        }

        private void OScope_SaveWaveformCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            drawGraph = false;
            OScope_DisableGraphAndUIElements();  // disable all the UI elements so the user can't mess with the capture
            scope.Stop();  // first stop the scope
            RunLabel.Content = "Stopped";
            scope.SetActiveChannel(scopeChannelInFocus);  // determine which channel we are capturing (only one at a time)
            int memDepth = scope.GetMemDepth();  // retrieve the memory depth of the scope
            ThreadPool.QueueUserWorkItem(lamda =>
            {
                lock (downloadLock)
                {
                    double xInc = scope.GetXIncrement();
                    double[] voltages = scope.GetDeepMemVoltages(scopeChannelInFocus);
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
                    OScope_EnableGraphAndUIElements();
                }
            });


        }


        private void OScope_DisableGraphAndUIElements()
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

        private void OScope_EnableGraphAndUIElements()
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
