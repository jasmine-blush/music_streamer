using System.ComponentModel;
using System.IO;
using System.Windows;

namespace MusicStreamerClientWPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        //Used to display library in window
        public string[] SongList
        {
            get { return Mp3Streamer.SongsArray; }
            set {
                Mp3Streamer.SongsArray = value;
                OnPropertyChanged(nameof(SongList));
            }
        }

        //Used to display currently playing song in window
        private string _playingText = "";
        public string PlayingText
        {
            get { return _playingText; }
            set {
                _playingText = value;
                OnPropertyChanged(nameof(PlayingText));
            }
        }

        //Used to display current slider value in window and adjust volume in Mp3Streamer
        private string _sliderValueText = "20%";
        public string SliderValueText
        {
            get { return _sliderValueText; }
            set {
                _sliderValueText = value;
                OnPropertyChanged(nameof(SliderValueText));
            }
        }
        private int _sliderValue = 20;
        public int SliderValue
        {
            get { return _sliderValue; }
            set {
                _sliderValue = value;
                SliderValueText = value + "%";
                Mp3Streamer.ChangeVolume(value);
                OnPropertyChanged(nameof(SliderValue));
            }
        }

        //Used to display cover image of current song, or "no_image" image in window
        private static readonly byte[] _noImage = File.ReadAllBytes("no_image.png");
        private byte[] _coverSource = [];
        public byte[] CoverSource
        {
            get { return _coverSource; }
            set {
                _coverSource = value;
                OnPropertyChanged(nameof(CoverSource));
            }
        }

        //Queued songs list
        private readonly List<QueueItem> _queue = [];
        private static readonly object _queueLock = new();
        
        private struct QueueItem(int index)
        {
            internal int Index = index; //Index in SongList (song library)
            internal bool HasPic = true;
            internal byte[]? Pic = null;
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //Start receiving data from server and playing received songs
            ThreadPool.QueueUserWorkItem(Mp3Streamer.LoadMusic, this);
            ThreadPool.QueueUserWorkItem(Mp3Streamer.Play, this);
        }

        /// <summary>
        /// Queues currently selected song in SongListBox
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void QueueButton_Click(object sender, RoutedEventArgs e)
        {
            if(SongListBox.SelectedIndex > -1)
            {
                Mp3Streamer.QueueSong(SongListBox.SelectedIndex);
            }
        }

        /// <summary>
        /// Removes current song from queue, then adjusts PlayingText and CoverSource to match next song in queue.
        /// Called by Mp3Streamer playback thread when a song has finished.
        /// </summary>
        internal void SongFinished()
        {
            lock(_queueLock)
            {
                try
                {
                    _queue.RemoveAt(0);
                    if(_queue.Count > 0)
                    {
                        PlayingText = "Playing: " + Mp3Streamer.SongsArray[_queue[0].Index];
                        if(_queue[0].HasPic)
                        {
                            CoverSource = _queue[0].Pic;
                        }
                        else
                        {
                            CoverSource = _noImage;
                        }
                    }
                    else
                    {
                        PlayingText = "";
                        CoverSource = [];
                    }
                }
                catch(ArgumentOutOfRangeException) { }
            }
        }

        /// <summary>
        /// Adds new item to _queue
        /// </summary>
        /// <param name="index">Index used for QueueItem to be added</param>
        internal void AddQueueItem(int index)
        {
            lock(_queueLock)
            {
                _queue.Add(new QueueItem(index));
                if(_queue.Count == 1)
                {
                    PlayingText = "Playing: " + Mp3Streamer.SongsArray[_queue[0].Index];
                }
            }
        }


        /// <summary>
        /// Adds picture to the first item in the queue where HasPic is true and Pic is still null
        /// </summary>
        /// <param name="pic">The picture to add to the queue item</param>
        internal void AddPic(byte[]? pic)
        {
            lock(_queueLock)
            {
                for(int i = 0; i < _queue.Count; i++)
                {
                    QueueItem queueItem = _queue[i];
                    if(queueItem.HasPic && queueItem.Pic == null)
                    {
                        _queue[i] = new(queueItem.Index){
                            Pic = pic
                        };
                        break;
                    }
                }
                if(_queue.Count == 1)
                {
                    CoverSource = _queue[0].Pic;
                }
            }
        }


        /// <summary>
        /// Changes HasPic to false to the first queue item where Pic is still null
        /// </summary>
        internal void FlagNoPic()
        {
            lock(_queueLock)
            {
                for(int i = 0; i < _queue.Count; i++)
                {
                    QueueItem queueItem = _queue[i];
                    if(queueItem.HasPic && queueItem.Pic == null)
                    {
                        _queue[i] = new QueueItem(queueItem.Index) {
                            HasPic = false
                        };
                        break;
                    }
                }
                if(_queue.Count == 1)
                {
                    CoverSource = _noImage;
                }
            }
        }
    }
}