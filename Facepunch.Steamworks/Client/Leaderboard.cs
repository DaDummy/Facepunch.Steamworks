﻿using System;
using System.Collections.Generic;
using System.Linq;
using SteamNative;

namespace Facepunch.Steamworks
{
    public class Leaderboard : IDisposable
    {
        /// <summary>
        ///     Type of leaderboard request
        /// </summary>
        public enum RequestType
        {
            /// <summary>
            ///     Query everyone and everything
            /// </summary>
            Global = LeaderboardDataRequest.Global,

            /// <summary>
            ///     Query entries near to this user's rank
            /// </summary>
            GlobalAroundUser = LeaderboardDataRequest.GlobalAroundUser,

            /// <summary>
            ///     Only show friends of this user
            /// </summary>
            Friends = LeaderboardDataRequest.Friends
        }

        private static readonly int[] subEntriesBuffer = new int[512];

        internal ulong BoardId;
        internal Client client;

        /// <summary>
        ///     The results from the last query. Can be null.
        /// </summary>
        public Entry[] Results;

        internal Leaderboard( Client c )
        {
            client = c;
        }

        /// <summary>
        ///     The name of this board, as retrieved from Steam
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        ///     The total number of entries on this board
        /// </summary>
        public int TotalEntries { get; private set; }

        /// <summary>
        ///     Returns true if this board is valid, ie, we've received
        ///     a positive response from Steam about it.
        /// </summary>
        public bool IsValid => BoardId != 0;

        /// <summary>
        ///     Returns true if we asked steam about this board but it returned
        ///     an error.
        /// </summary>
        public bool IsError { get; private set; }

        /// <summary>
        ///     Returns true if we're querying scores
        /// </summary>
        public bool IsQuerying { get; private set; }

        public void Dispose()
        {
            client = null;
        }

        internal void OnBoardCreated( LeaderboardFindResult_t result, bool error )
        {
            if ( error || ( result.LeaderboardFound == 0 ) )
            {
                IsError = true;
                return;
            }

            BoardId = result.SteamLeaderboard;

            if ( IsValid )
            {
                Name = client.native.userstats.GetLeaderboardName( BoardId );
                TotalEntries = client.native.userstats.GetLeaderboardEntryCount( BoardId );
            }
        }

        /// <summary>
        /// Add a score to this leaderboard.
        /// Subscores are totally optional, and can be used for other game defined data such as laps etc.. although
        /// they have no bearing on sorting at all
        /// If onlyIfBeatsOldScore is true, the score will only be updated if it beats the existing score, else it will always
        /// be updated.
        /// </summary>
        public bool AddScore( bool onlyIfBeatsOldScore, int score, params int[] subscores )
        {
            if ( !IsValid ) return false;

            var flags = LeaderboardUploadScoreMethod.ForceUpdate;
            if ( onlyIfBeatsOldScore ) flags = LeaderboardUploadScoreMethod.KeepBest;

            client.native.userstats.UploadLeaderboardScore( BoardId, flags, score, subscores, subscores.Length );

            return true;
        }

        /// <summary>
        /// Callback invoked by <see cref="AddScore(bool, int, int[], AddScoreCallback)"/> when score submission
        /// is complete.
        /// </summary>
        /// <param name="success">If true, the score was submitted</param>
        /// <param name="result">If successful, information about the new entry</param>
        public delegate void AddScoreCallback( bool success, AddScoreResult result );

        /// <summary>
        /// Information about a newly submitted score.
        /// </summary>
        public struct AddScoreResult
        {
            public int Score;
            public bool ScoreChanged;
            public int GlobalRankNew;
            public int GlobalRankPrevious;
        }

        /// <summary>
        /// Add a score to this leaderboard.
        /// Subscores are totally optional, and can be used for other game defined data such as laps etc.. although
        /// they have no bearing on sorting at all
        /// If onlyIfBeatsOldScore is true, the score will only be updated if it beats the existing score, else it will always
        /// be updated.
        /// Information about the newly submitted score is passed to the optional <paramref name="callback"/>.
        /// </summary>
        public bool AddScore( bool onlyIfBeatsOldScore, int score, int[] subscores = null, AddScoreCallback callback = null )
        {
            if ( !IsValid ) return false;

            if ( subscores == null ) subscores = new int[0];

            var flags = LeaderboardUploadScoreMethod.ForceUpdate;
            if ( onlyIfBeatsOldScore ) flags = LeaderboardUploadScoreMethod.KeepBest;

            client.native.userstats.UploadLeaderboardScore( BoardId, flags, score, subscores, subscores.Length, callback != null ? (Action<LeaderboardScoreUploaded_t, bool>) (( result, error ) =>
            {
                callback( !error && result.Success != 0, new AddScoreResult
                {
                    Score = result.Score,
                    ScoreChanged = result.ScoreChanged != 0,
                    GlobalRankNew = result.GlobalRankNew,
                    GlobalRankPrevious = result.GlobalRankPrevious
                } );
            }) : null );

            return true;
        }

        /// <summary>
        /// Callback invoked by <see cref="Leaderboard.AttachRemoteFile"/> when file attachment is complete.
        /// </summary>
        public delegate void AttachRemoteFileCallback( bool success );

        /// <summary>
        /// Attempt to attach a <see cref="RemoteStorage"/> file to the current user's leaderboard entry.
        /// Can be useful for storing replays along with scores.
        /// </summary>
        /// <returns>True if the file attachment process has started</returns>
        public bool AttachRemoteFile( RemoteFile file, AttachRemoteFileCallback callback = null )
        {
            if ( !IsValid ) return false;

            if ( file.IsShared )
            {
                var handle = client.native.userstats.AttachLeaderboardUGC( BoardId, file.UGCHandle, ( result, error ) =>
                {
                    callback?.Invoke( !error && result.Result == Result.OK );
                } );

                return handle.CallResultHandle != 0;
            }

            file.Share( success =>
            {
                if ( !success || !file.IsShared || !AttachRemoteFile( file, callback ) )
                {
                    callback?.Invoke( false );
                }
            } );
            return true;
        }

        /// <summary>
        ///     Fetch a subset of scores. The scores end up in Results.
        /// </summary>
        /// <returns>Returns true if we have started the query</returns>
        public bool FetchScores( RequestType RequestType, int start, int end )
        {
            if ( !IsValid ) return false;
            if ( IsQuerying ) return false;

            client.native.userstats.DownloadLeaderboardEntries( BoardId, (LeaderboardDataRequest) RequestType, start, end, OnScores );

            Results = null;
            IsQuerying = true;
            return true;
        }

        private unsafe void ReadScores( LeaderboardScoresDownloaded_t result, List<Entry> dest )
        {
            for ( var i = 0; i < result.CEntryCount; i++ )
                fixed ( int* ptr = subEntriesBuffer )
                {
                    var entry = new LeaderboardEntry_t();
                    if ( client.native.userstats.GetDownloadedLeaderboardEntry( result.SteamLeaderboardEntries, i, ref entry, (IntPtr) ptr, subEntriesBuffer.Length ) )
                        dest.Add( new Entry
                        {
                            GlobalRank = entry.GlobalRank,
                            Score = entry.Score,
                            SteamId = entry.SteamIDUser,
                            SubScores = entry.CDetails == 0 ? null : subEntriesBuffer.Take( entry.CDetails ).ToArray(),
                            Name = client.Friends.GetName( entry.SteamIDUser ),
                            AttachedFile = (entry.UGC >> 32) == 0xffffffff ? null : new RemoteFile( client.RemoteStorage, entry.UGC )
                        } );
                }
        }

        [ThreadStatic] private static List<Entry> _sEntryBuffer;

        /// <summary>
        /// Callback invoked by <see cref="FetchScores(RequestType, int, int, FetchScoresCallback)"/> when
        /// a query is complete.
        /// </summary>
        public delegate void FetchScoresCallback( bool success, Entry[] results );

        /// <summary>
        ///     Fetch a subset of scores. The scores are passed to <paramref name="callback"/>.
        /// </summary>
        /// <returns>Returns true if we have started the query</returns>
        public bool FetchScores( RequestType RequestType, int start, int end, FetchScoresCallback callback )
        {
            if ( !IsValid ) return false;

            client.native.userstats.DownloadLeaderboardEntries( BoardId, (LeaderboardDataRequest) RequestType, start, end, ( result, error ) =>
            {
                if ( error )
                {
                    callback( false, null );
                }
                else
                {
                    if ( _sEntryBuffer == null ) _sEntryBuffer = new List<Entry>();
                    else _sEntryBuffer.Clear();

                    ReadScores( result, _sEntryBuffer );
                    callback( true, _sEntryBuffer.ToArray() );
                }
            } );

            return true;
        }

        private void OnScores( LeaderboardScoresDownloaded_t result, bool error )
        {
            IsQuerying = false;

            if ( client == null ) return;
            if ( error ) return;

            var list = new List<Entry>();
            ReadScores( result, list );

            Results = list.ToArray();
        }

        /// <summary>
        ///     A single entry in a leaderboard
        /// </summary>
        public struct Entry
        {
            public ulong SteamId;
            public int Score;
            public int[] SubScores;
            public int GlobalRank;
            public RemoteFile AttachedFile;

            /// <summary>
            ///     Note that the player's name might not be immediately available.
            ///     If that's the case you'll have to use Friends.GetName to find the name
            /// </summary>
            public string Name;
        }
    }
}
