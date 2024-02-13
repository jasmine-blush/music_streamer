using ATL;

namespace MusicStreamerServer
{
    internal class Player
    {
        internal static byte[] CurrBytes = new byte[2048];
        internal static readonly object CurrBytesLock = new();
        internal static bool CurrBytesChanged = false;
        internal static Track CurrTrack;
        internal static int CurrIndex = -1;

        private static readonly List<byte[]> _songQueue = [];
        private static readonly object _songQueueLock = new();

        internal static List<string> FileList = []; //String list of song library with full file path 
        internal static List<string> SongList = []; //String list of song library with just song names
        

        internal static void StartPlaying()
        {
            do
            {
                if(!Server.SongFinished && _songQueue.Count > 0) //(Re-)start playback as soon as the last song is finished and a new one is queued
                {
                    ThreadPool.QueueUserWorkItem(Play);
                    Server.StreamSong();
                }
            } while(true);
        }


        /// <summary>
        /// Loads next song in queue and reads it into CurrBytes at a set intervall
        /// </summary>
        /// <param name="state">Unused</param>
        private static void Play(object? state)
        {
            //Load song
            byte[] song;
            lock(_songQueueLock)
            {
                song = _songQueue[0];
            }
            CurrTrack = new(new MemoryStream(song));

            //Read the next 2048 bytes into CurrBytes every 50ms until song is over (~320kbit/s)
            //TODO: allow for different bitrates
            int currIndex = 0;
            do
            {
                int newIndex = currIndex + 2048;
                lock(CurrBytesLock)
                {
                    if(newIndex >= song.Length)
                    {
                        CurrBytes = song[currIndex..^0];
                        CurrBytesChanged = true;
                        break;
                    }
                    CurrBytes = song[currIndex..newIndex];
                    CurrBytesChanged = true;
                }
                currIndex = newIndex;
                Thread.Sleep(50);
            } while(currIndex < song.Length);

            //Remove song from queue and end playback
            lock(_songQueueLock)
            {
                _songQueue.RemoveAt(0);
            }
            Server.SongFinished = true;
        }

        /// <summary>
        /// Queues a song into _songQueue to be used by the playback Thread
        /// </summary>
        /// <param name="index">Index of the song to be queued of Player.FileList</param>
        internal static void QueueSong(int index)
        {
            if(index < FileList.Count)
            {
                if(CurrIndex == -1)
                {
                    CurrIndex = index;
                }
                byte[] file = File.ReadAllBytes(FileList[index]);
                Track track = new(new MemoryStream(file));
                Server.SendNextQueue(index, track);

                lock(_songQueueLock)
                {
                    _songQueue.Add(file);
                }
                Console.WriteLine("Queued: " + SongList[index]);
            }
        }
            

        /// <summary>
        /// Asks user for path to song library, then loads file paths into FileList and song names into SongList
        /// </summary>
        internal static void LoadSongs()
        {
            //Ask for path to song library
            Console.Write("Song Library Path: ");
            string? path = Console.ReadLine();

            while(!Directory.Exists(path))
            {
                Console.Write("\nInvalid Directory, Song Library Path: ");
                path = Console.ReadLine();
            }

            //Load into FileList and SongList
            foreach(string file in Directory.EnumerateFiles(path))
            {
                if(file.EndsWith(".mp3"))
                {
                    FileList.Add(file);
                    SongList.Add(file.Split('\\')[^1].Split(".mp3")[0]);
                }
            }
        }
    }
}
