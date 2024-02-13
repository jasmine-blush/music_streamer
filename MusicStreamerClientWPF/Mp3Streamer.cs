using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MusicStreamerClientWPF
{
    internal class Mp3Streamer
    {
        private static WaveOutEvent? _outputDevice = null;
        private static BufferedWaveProvider? _bwp = null;
        private static int _currentSampleRate;

        private static readonly List<byte> _loadedBytes = [];
        private static readonly object _loadedBytesLock = new();
        private static bool _songEnded = false;
        private static readonly List<byte> _nextSongLoadedBytes = [];

        internal static string[] SongsArray = [];

        private static Socket _socket;

        private enum DATA_CODE : byte //Identifier codes for packets sent to clients
        {
            INVALID = 0,
            SONG = 1, //packet with song-bytes
            SONGLIST = 2, //partial packet for song library transfer
            SONGLIST_END = 3, //song library fully transferred signal
            NEXTQUEUE = 4, //packet with index of next queued song
            NEXTPIC = 5, //partial packet for cover image transfer of next queued song
            NEXTPIC_END = 6, //cover image of next queued song fully transferred signal
            SONG_END = 7, //current song fully transferred signal
        }

        /// <summary>
        /// Infinitely adds the next Mp3Frame from _loadedBytes to the BufferedWaveProvider _bwp to be played by the _outputDevice
        /// </summary>
        internal static void Play(object state)
        {
            MainWindow window = (MainWindow)state;

            var buffer = new byte[16384 * 4];
            AcmMp3FrameDecompressor? decompressor = null;

            do
            {
                if(IsBufferNearlyFull())
                {
                    Thread.Sleep(500);
                }
                else
                {
                    //Try to read a valid Mp3Frame from _loadedBytes
                    Mp3Frame? frame = null;
                    try
                    {
                        if(_loadedBytes.Count > 0)
                        {
                            lock(_loadedBytesLock)
                            {
                                MemoryStream currStream = new(_loadedBytes.ToArray());
                                frame = Mp3Frame.LoadFromStream(currStream);
                                if(frame != null)
                                {
                                    _loadedBytes.RemoveRange(0, (int)currStream.Position);
                                }
                            }
                        }
                    }
                    catch(EndOfStreamException) {
                        Thread.Sleep(1);
                    }

                    //Loads already received bytes from next song into _loadedBytes when current song has ended (basically a double-buffer system)
                    if(_songEnded && _bwp.BufferedBytes == 0)
                    {
                        _songEnded = false;
                        _loadedBytes.AddRange(_nextSongLoadedBytes);
                        _nextSongLoadedBytes.Clear();
                        window.SongFinished();
                    }

                    if(frame != null) //If a valid Mp3Frame has been read
                    {
                        //If the decompressor hasn't been initialized yet or the SampleRate has changed during the song, re-initialize sound output
                        if(decompressor == null || _currentSampleRate != frame.SampleRate)
                        {
                            _currentSampleRate = frame.SampleRate;
                            decompressor = CreateFrameDecompressor(frame);
                            _bwp = new(decompressor.OutputFormat) {
                                BufferDuration = TimeSpan.FromSeconds(20)
                            };
                            if(_outputDevice != null)
                            {
                                _outputDevice.Stop();
                                _outputDevice.Dispose();
                            }
                            _outputDevice = new();
                            _outputDevice.Init(_bwp);
                            _outputDevice.Volume = window.SliderValue / 100f;
                            _outputDevice.DesiredLatency = 50;
                            _outputDevice.Play();
                        }

                        //Then try to decompress the Mp3Frame and add it to playback buffer
                        try
                        {
                            int decompressedBytes = decompressor.DecompressFrame(frame, buffer, 0);
                            _bwp.AddSamples(buffer, 0, decompressedBytes);
                        }
                        catch(Exception) { }
                    }

                    Thread.Sleep(10);
                }
            } while(true);
        }

        /// <summary>
        /// Changes Volume of output device
        /// </summary>
        /// <param name="volume">Volume between 1 and 100</param>
        internal static void ChangeVolume(int volume)
        {
            if(_outputDevice != null)
            {
                _outputDevice.Volume = volume / 100f;
            }
        }

        #region Utility
        /// <summary>
        /// Creates a new AcmMp3FrameDecompressor matching a given Mp3Frame
        /// </summary>
        /// <param name="frame">Mp3Frame to use the metadata from to create the decompressor</param>
        /// <returns>Returns the newly created AcmMp3FrameDecompressor</returns>
        private static AcmMp3FrameDecompressor CreateFrameDecompressor(Mp3Frame frame)
        {
            WaveFormat waveFormat = new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2,
            frame.FrameLength, frame.BitRate);
            return new AcmMp3FrameDecompressor(waveFormat);
        }

        /// <summary>
        /// Checks whether the _bwp buffer is nearly full
        /// </summary>
        /// <returns>Returns whether it is nearly full or not</returns>
        private static bool IsBufferNearlyFull()
        {
            return _bwp != null &&
                    _bwp.BufferLength - _bwp.BufferedBytes
                    < _bwp.WaveFormat.AverageBytesPerSecond / 4;
        }
        #endregion Utility

        /// <summary>
        /// Opens connection to server and handles all received packets
        /// </summary>
        /// <param name="state">Reference to main window</param>
        internal static void LoadMusic(object state)
        {
            MainWindow window = (MainWindow)state;

            //Connect to server
            IPEndPoint serverEndPoint = new(IPAddress.Parse("127.0.0.1"), 35555);
            IPEndPoint clientEndPoint = new(IPAddress.Parse("127.0.0.1"), 35556);
            _socket = new(clientEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _socket.Connect(serverEndPoint);

            //Receive Data in a loop
            List<byte> songsReceiveList = [];
            List<byte> picReceiveList = [];
            try
            {
                do
                {
                    //Fetch currently buffered data from Socket and split it into packets
                    byte[] data = new byte[20490];
                    var receivedBytes = _socket.Receive(data, SocketFlags.None);

                    List<byte[]> receivedPackets = [];
                    for(int i = 0; i < data.Length; i += 2049)
                    {
                        if(data[i] == (byte)DATA_CODE.INVALID)
                        {
                            break;
                        }
                        int endIndex = i + 2049;
                        receivedPackets.Add(data[i..endIndex]);
                    }

                    //Go through each packet and handle it according to its DATA_CODE
                    foreach(byte[] packet in receivedPackets)
                    {
                        switch(packet[0])
                        {
                            case (byte)DATA_CODE.SONG: //Add song data to currently active buffer
                                if(!_songEnded)
                                {
                                    lock(_loadedBytesLock)
                                    {
                                        _loadedBytes.AddRange(packet[1..^0]);
                                    }
                                }
                                else
                                {
                                    _nextSongLoadedBytes.AddRange(packet[1..^0]);
                                }
                                
                                break;

                            case (byte)DATA_CODE.SONGLIST: //Receive parts of the song library
                                songsReceiveList.AddRange(packet);
                                break;

                            case (byte)DATA_CODE.SONGLIST_END: //Assemble fully received song library
                                if(songsReceiveList.Count > 0)
                                {
                                    string songsString = Encoding.UTF8.GetString(songsReceiveList.ToArray()).TrimEnd();
                                    window.SongList = songsString.Split(';')[0..^0];
                                    songsReceiveList.Clear();
                                }
                                break;

                            case (byte)DATA_CODE.NEXTQUEUE: //Add new song to queue
                                window.AddQueueItem(BitConverter.ToInt32(packet.AsSpan()[1..^5]));
                                break;

                            case (byte)DATA_CODE.NEXTPIC: //Receive parts of a cover image
                                picReceiveList.AddRange(packet[1..^0]);
                                break;

                            case (byte)DATA_CODE.NEXTPIC_END: //Assemble cover image and add it to respective song
                                if(picReceiveList.Count > 0)
                                {
                                    window.AddPic(picReceiveList.ToArray());
                                    picReceiveList.Clear();
                                }
                                else
                                {
                                    window.FlagNoPic();
                                }
                                break;

                            case (byte)DATA_CODE.SONG_END: //Current song has ended, swap buffer
                                _songEnded = true;
                                break;
                        }
                    }
                } while(true);
            }
            catch(Exception e)
            {
                Console.WriteLine(e.ToString());
            }
            finally
            {
                _socket.Close();
            }
        }

        /// <summary>
        /// Queues a song by index in SongsArray
        /// </summary>
        /// <param name="index">Index of the song in SongsArray</param>
        internal static void QueueSong(int index)
        {
            _socket.Send(BitConverter.GetBytes(index), SocketFlags.None);
        }
    }
}
