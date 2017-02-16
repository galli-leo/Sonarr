﻿using NzbDrone.Common.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.NetImport.Trakt
{
    public class TraktRequestGenerator : INetImportRequestGenerator
    {
        public IConfigService _configService;
        public TraktSettings Settings { get; set; }

        public virtual NetImportPageableRequestChain GetMovies()
        {
            var pageableRequests = new NetImportPageableRequestChain();

            pageableRequests.Add(GetMovies(null));

            return pageableRequests;
        }

        private IEnumerable<NetImportRequest> GetMovies(string searchParameters)
        {
            var link = Settings.Link.Trim();

            switch (Settings.ListType)
            {
                case (int)TraktListType.UserCustomList:
                    link = link + $"/users/{Settings.Username.Trim()}/lists/{Settings.Listname.Trim()}/items/movies";
                    break;
                case (int)TraktListType.UserWatchList:
                    link = link + $"/users/{Settings.Username.Trim()}/watchlist/movies";
                    break;
                case (int)TraktListType.UserWatchedList:
                    link = link + $"/users/{Settings.Username.Trim()}/watched/movies";
                    break;
                case (int)TraktListType.Trending:
                    link = link + "/movies/trending";
                    break;
                case (int)TraktListType.Popular:
                    link = link + "/movies/popular";
                    break;
                case (int)TraktListType.Anticipated:
                    link = link + "/movies/anticipated";
                    break;
                case (int)TraktListType.BoxOffice:
                    link = link + "/movies/boxoffice";
                    break;
                case (int)TraktListType.TopWatchedByWeek:
                    link = link + "/movies/watched/weekly";
                    break;
                case (int)TraktListType.TopWatchedByMonth:
                    link = link + "/movies/watched/monthly";
                    break;
                case (int)TraktListType.TopWatchedByYear:
                    link = link + "/movies/watched/yearly";
                    break;
                case (int)TraktListType.TopWatchedByAllTime:
                    link = link + "/movies/watched/all";
                    break;
            }

            if (_configService.TraktRefreshToken != null) 
            {
                // TimeSpan span = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0,DateTimeKind.Utc));
                //double unixTime = span.TotalSeconds;
                Int32 unixTime= (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;

                if ( unixTime > _configService.TraktTokenExpiry)
                {
                    //var url = Settings.Link.Trim();
                    //url = url + "/oauth/token";
                    var url = "https://api.couchpota.to/authorize/trakt_refresh?token="+_configService.TraktRefreshToken;
                    ////var options= "?token="+_configService.TraktRefreshToken;
                    //this code is not going to work right now -- need to implement with apiServer
                    //string postData = "{\"refresh_token\":\""+ _configService.TraktRefreshToken+"\"";
                    //postData += ",\"client_id\":\"657bb899dcb81ec8ee838ff09f6e013ff7c740bf0ccfa54dd41e791b9a70b2f0\""; //radarr
                    //postData += ",\"client_id\":\"8a54ed7b5e1b56d874642770ad2e8b73e2d09d6e993c3a92b1e89690bb1c9014\""; //couchpotato
                    //postData += ",\"redirect_uri\":\"urn:ietf:wg:oauth:2.0:oob\"";
                    //postData += ",\"grant_type\":\"refresh_token\"}";

                    //byte[] byteArray = Encoding.UTF8.GetBytes(postData);

                    HttpWebRequest rquest = (HttpWebRequest)WebRequest.Create(url);
                    //rquest.Method = "GET";
                    //rquest.ContentType = "application/json";
                    //using (var st = rquest.GetRequestStream())
                    //    st.Write(byteArray, 0, byteArray.Length);
                    //var rsponse = (HttpWebResponse)rquest.GetResponse();
                    //var rsponseString = new StreamReader(rsponse.GetResponseStream()).ReadToEnd();
                    //rsponse.Close();
                    //dynamic j1 = JObject.Parse(rsponseString);
                    string rsponseString = string.Empty;
                    using (HttpWebResponse rsponse = (HttpWebResponse)rquest.GetResponse())
                    using (Stream stream = rsponse.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        rsponseString = reader.ReadToEnd();
                    }
                    dynamic j1 = JObject.Parse(rsponseString);
                    _configService.TraktAuthToken = j1.oauth;
                    _configService.TraktRefreshToken = j1.refresh;
                    //int createdAt = unixTime;
                    //string createdAt = j1.created_at;
                    //string expiresIn = j1.expires_in;
                    //lets have it expire in 8 weeks (4838400 seconds)
                    _configService.TraktTokenExpiry = unixTime + 4838400;//int.Parse(expiresIn);
                }
            }

            var request = new NetImportRequest($"{link}", HttpAccept.Json);
            request.HttpRequest.Headers.Add("trakt-api-version", "2");
            //request.HttpRequest.Headers.Add("trakt-api-key", "657bb899dcb81ec8ee838ff09f6e013ff7c740bf0ccfa54dd41e791b9a70b2f0"); //radarr
	    request.HttpRequest.Headers.Add("trakt-api-key", "8a54ed7b5e1b56d874642770ad2e8b73e2d09d6e993c3a92b1e89690bb1c9014"); //couchpotato
            if (_configService.TraktAuthToken != null)
            {
                request.HttpRequest.Headers.Add("Authorization", "Bearer " + _configService.TraktAuthToken);
            }

                yield return request;
        }
    }
}
