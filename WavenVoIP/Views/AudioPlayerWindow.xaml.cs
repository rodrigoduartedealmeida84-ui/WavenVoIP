using NAudio.Wave;
using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WavenVoIP.Views
{
    public partial class AudioPlayerWindow : Window
    {
        private WaveOutEvent?    _waveOut;
        private AudioFileReader? _audioFile;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private bool _updatingFromTimer;
        private bool _disposed;

        public AudioPlayerWindow(string filePath)
        {
            InitializeComponent();
            txtFileName.Text = Path.GetFileName(filePath);
            _timer.Tick += Timer_Tick;
            Closed  += (_, _) => StopAndDispose();
            Loaded  += (_, _) => IniciarPlayback(filePath);
        }

        private void IniciarPlayback(string filePath)
        {
            try
            {
                StopAndDispose();
                _disposed = false;

                if (!File.Exists(filePath))
                {
                    App.LogCrash("RECORDING_PLAY_ERROR", $"Arquivo não encontrado: {filePath}", null);
                    MessageBox.Show("Arquivo de gravação não encontrado.", "Waven VoIP — Player",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    Close();
                    return;
                }

                _audioFile = new AudioFileReader(filePath);
                _waveOut   = new WaveOutEvent();
                _waveOut.Volume = (float)sldVolume.Value;
                _waveOut.Init(_audioFile);
                sldProgress.Maximum  = _audioFile.TotalTime.TotalSeconds;
                sldProgress.IsEnabled = true;
                AtualizarTempo();

                _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                _waveOut.Play();
                _timer.Start();
                btnPlayPause.Content = "⏸";
            }
            catch (Exception ex)
            {
                App.LogCrash("RECORDING_PLAY_ERROR", filePath, ex);
                try
                {
                    MessageBox.Show("Não foi possível reproduzir esta gravação.\n\nO arquivo pode estar corrompido ou em formato não suportado.",
                        "Waven VoIP — Player", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Close();
                }
                catch { }
            }
        }

        private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            // Fires on NAudio background thread — use BeginInvoke to avoid blocking/deadlock.
            try
            {
                if (e.Exception != null)
                    App.LogCrash("RECORDING_PLAYER_CRASH_PREVENTED", "PlaybackStopped com exceção", e.Exception);

                if (_disposed) return;

                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (_disposed || _waveOut == null || _audioFile == null) return;
                        btnPlayPause.Content = "▶";
                        _timer.Stop();
                        if (_audioFile.CurrentTime >= _audioFile.TotalTime.Add(TimeSpan.FromMilliseconds(-500)))
                        {
                            _audioFile.CurrentTime = TimeSpan.Zero;
                            _updatingFromTimer = true;
                            sldProgress.Value  = 0;
                            _updatingFromTimer = false;
                            AtualizarTempo();
                        }
                    }
                    catch (Exception ex)
                    {
                        App.LogCrash("RECORDING_PLAYER_CRASH_PREVENTED", "UI update em PlaybackStopped", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                App.LogCrash("RECORDING_PLAYER_CRASH_PREVENTED", "WaveOut_PlaybackStopped", ex);
            }
        }

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_waveOut == null || _disposed) return;
                if (_waveOut.PlaybackState == PlaybackState.Playing)
                {
                    _waveOut.Pause();
                    _timer.Stop();
                    btnPlayPause.Content = "▶";
                }
                else
                {
                    _waveOut.Play();
                    _timer.Start();
                    btnPlayPause.Content = "⏸";
                }
            }
            catch (Exception ex)
            {
                App.LogCrash("RECORDING_PLAYER_CRASH_PREVENTED", "BtnPlayPause_Click", ex);
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_disposed || _audioFile == null) return;
                _updatingFromTimer  = true;
                sldProgress.Value   = _audioFile.CurrentTime.TotalSeconds;
                _updatingFromTimer  = false;
                AtualizarTempo();
            }
            catch (Exception ex)
            {
                App.LogCrash("RECORDING_PLAYER_CRASH_PREVENTED", "Timer_Tick", ex);
                _updatingFromTimer = false;
            }
        }

        private void AtualizarTempo()
        {
            try
            {
                if (_disposed || _audioFile == null) return;
                var cur = _audioFile.CurrentTime;
                var tot = _audioFile.TotalTime;
                txtTime.Text = $"{(int)cur.TotalMinutes:D2}:{cur.Seconds:D2} / {(int)tot.TotalMinutes:D2}:{tot.Seconds:D2}";
            }
            catch { }
        }

        private void SldProgress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (_updatingFromTimer || _disposed || _audioFile == null) return;
                _audioFile.CurrentTime = TimeSpan.FromSeconds(e.NewValue);
            }
            catch (Exception ex)
            {
                App.LogCrash("RECORDING_PLAYER_CRASH_PREVENTED", "SldProgress_ValueChanged", ex);
            }
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (_disposed || _waveOut == null) return;
                _waveOut.Volume = (float)e.NewValue;
            }
            catch { }
        }

        private void BtnFechar_Click(object sender, RoutedEventArgs e) => Close();

        private void StopAndDispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer.Stop();

            var wo = _waveOut;
            var af = _audioFile;
            _waveOut   = null;
            _audioFile = null;

            try { wo?.Stop(); }    catch { }
            try { wo?.Dispose(); } catch { }
            try { af?.Dispose(); } catch { }
        }
    }
}
