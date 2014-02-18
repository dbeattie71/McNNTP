﻿using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Configuration;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NHibernate;
using NHibernate.Cfg;
using NHibernate.Linq;
using McNNTP.Server.Data;
using System.Globalization;
using NHibernate.Tool.hbm2ddl;

namespace McNNTP.Server
{
    public class NntpServer
    {
        private static readonly object _sessionFactoryLock = new object();
        private static ISessionFactory _sessionFactory;

        private readonly Dictionary<string, Func<Connection, string, CommandProcessingResult>> _commandDirectory;

        private CommandProcessingResult _inProcessCommand;

        public bool AllowPosting { get; set; }
        public string ServerPath { get; set; }

        public NntpServer()
        {
            _commandDirectory = new Dictionary<string, Func<Connection, string, CommandProcessingResult>>
                {
                    {"ARTICLE", Article},
                    {"BODY", Body},
                    {"DATE", (s, c) => Date(s)},
                    {"CAPABILITIES", (s, c) => Capabilities(s)},
                    {"HDR", Hdr},
                    {"HEAD", Head},
                    {"HELP", (s, c) => Help(s)},
                    {"LIST", List},
                    {"LISTGROUP", ListGroup},
                    {"GROUP", Group},
                    {"MODE", Mode},
                    {"NEWGROUPS", Newgroups},
                    {"POST", Post},
                    {"STAT", Stat},
                    {"XOVER", XOver},
                    {"QUIT", (s, c) => Quit(s)}
                };

            // TODO: Put this in a custom config section
            ServerPath = "freenews.localhost";

            ShowData= true;
        }


        // Thread signal.
        private readonly ManualResetEvent _allDone = new ManualResetEvent(false);

        #region Connection and IO
        private static readonly List<Connection> _connections = new List<Connection>();

        public void StartListening(int port)
        {
            // Establish the local endpoint for the socket.
            // The DNS name of the computer
            // running the listener is "host.contoso.com".
            //var ipHostInfo = Dns.GetHostEntry(Dns.GetHostName());
            var localEndPoint = new IPEndPoint(IPAddress.Any, port);

            // Create a TCP/IP socket.
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                Blocking = false
            };

            // Bind the socket to the local endpoint and listen for incoming connections.
            try
            {
                listener.Bind(localEndPoint);
                listener.Listen(100);

                while (true)
                {
                    // Set the event to nonsignaled state.
                    _allDone.Reset();

                    // Start an asynchronous socket to listen for connections.
                    Console.WriteLine("Waiting for a connection on interface {0}:{1}... ", localEndPoint.Address, localEndPoint.Port);
                    listener.BeginAccept(AcceptCallback, listener);

                    // Wait until a connection is made before continuing.
                    _allDone.WaitOne();
                }

            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }

            Console.WriteLine("\nPress ENTER to continue...");
            Console.Read();

        }
        private void AcceptCallback(IAsyncResult ar)
        {
            // Signal the main thread to continue.
            _allDone.Set();

            // Get the socket that handles the client request.
            var listener = (Socket)ar.AsyncState;
            var handler = listener.EndAccept(ar);
            //Thread.CurrentThread.Name = string.Format("{0}:{1}", ((IPEndPoint)handler.RemoteEndPoint).Address, ((IPEndPoint)handler.RemoteEndPoint).Port);

            // Create the state object.
            var state = new Connection
            {
                CanPost = AllowPosting,
                WorkSocket = handler
            };
            _connections.Add(state);

// ReSharper disable ConvertIfStatementToConditionalTernaryExpression
            if (state.CanPost)
// ReSharper restore ConvertIfStatementToConditionalTernaryExpression
                Send(handler, "200 Service available, posting allowed\r\n");
            else
                Send(handler, "201 Service available, posting prohibited\r\n");

            try
            {
                handler.BeginReceive(state.Buffer, 0, Connection.BUFFER_SIZE, 0, ReadCallback, state);
            }
            catch (SocketException se)
            {
                Send(handler, "403 Archive server temporarily offline\r\n");
                Console.WriteLine(se.ToString());
            }
        }
        private void ReadCallback(IAsyncResult ar)
        {
            // Retrieve the state object and the handler socket
            // from the asynchronous state object.
            var connection = (Connection)ar.AsyncState;
            if (connection.WorkSocket == null)
                return;

            var handler = connection.WorkSocket;
            if (handler == null)
                return;

            // Read data from the client socket.
            int bytesRead;
            try
            {
                bytesRead = handler.EndReceive(ar);
            }
            catch (SocketException)
            {
                Send(handler, "403 Archive server temporarily offline\r\n");
                return;
            }

            // There  might be more data, so store the data received so far.
            connection.Builder.Append(Encoding.ASCII.GetString(connection.Buffer, 0, bytesRead));

            // Not all data received OR no more but not yet ending with the delimiter. Get more.
            var content = connection.Builder.ToString();
            if (bytesRead == Connection.BUFFER_SIZE || (bytesRead == 0 && !content.EndsWith("\r\n", StringComparison.Ordinal)))
            {
                if (!handler.Connected)
                    return;

                try
                {
                    handler.BeginReceive(connection.Buffer, 0, Connection.BUFFER_SIZE, 0, ReadCallback, connection);
                }
                catch (SocketException sex)
                {
                    Send(handler, "403 Archive server temporarily offline\r\n");
                    Console.Write(sex.ToString());
                }
                return;
            }

            // All the data has been read from the 
            // client. Display it on the console.
            if (ShowBytes && ShowData)
                Console.WriteLine("{0}:{1} <<< {2} bytes: {3}", ((IPEndPoint)connection.WorkSocket.RemoteEndPoint).Address, ((IPEndPoint)connection.WorkSocket.RemoteEndPoint).Port, content.Length, content.TrimEnd('\r', '\n'));
            else if (ShowBytes)
                Console.WriteLine("{0}:{1} <<< {2} bytes", ((IPEndPoint)connection.WorkSocket.RemoteEndPoint).Address, ((IPEndPoint)connection.WorkSocket.RemoteEndPoint).Port, content.Length);
            else if (ShowData)
                Console.WriteLine("{0}:{1} <<< {2}", ((IPEndPoint)connection.WorkSocket.RemoteEndPoint).Address, ((IPEndPoint)connection.WorkSocket.RemoteEndPoint).Port, content.TrimEnd('\r', '\n'));

            if (_inProcessCommand != null && _inProcessCommand.MessageHandler != null)
            {
                // Ongoing read - don't parse it for commands
                var result = _inProcessCommand.MessageHandler.Invoke(connection, content, _inProcessCommand);
                if (result.IsQuitting)
                    _inProcessCommand = null;
            }
            else
            {
                var command = content.Split(' ').First().TrimEnd('\r', '\n');
                if (_commandDirectory.ContainsKey(command))
                {
                    try
                    {
                        if (ShowCommands)
                            Console.WriteLine("{0}:{1} <<< {2}", ((IPEndPoint)connection.WorkSocket.RemoteEndPoint).Address, ((IPEndPoint)connection.WorkSocket.RemoteEndPoint).Port, content.TrimEnd('\r', '\n'));
                                
                        var result = _commandDirectory[command].Invoke(connection, content);

                        if (!result.IsHandled)
                            Send(handler, "500 Unknown command\r\n");
                        else if (result.MessageHandler != null)
                            _inProcessCommand = result;
                        else if (result.IsQuitting)
                            return;
                    }
                    catch (Exception ex)
                    {
                        Send(handler, "403 Archive server temporarily offline\r\n");
                        Console.WriteLine(ex.ToString());
                    }
                }
                else
                    Send(handler, "500 Unknown command\r\n");
            }

            connection.Builder.Clear();

            if (!handler.Connected)
                return;

            // Not all data received. Get more.
            try
            {
                handler.BeginReceive(connection.Buffer, 0, Connection.BUFFER_SIZE, 0, ReadCallback, connection);
            }
            catch (SocketException sex)
            {
                Send(handler, "403 Archive server temporarily offline\r\n");
                Console.WriteLine(sex.ToString());
            }
        }
        private void Send(Socket handler, string data)
        {
            Send(handler, data, true, Encoding.UTF8);
        }
        private void Send(Socket handler, string data, bool async, Encoding encoding)
        {
            // Convert the string data to byte data using ASCII encoding.
            var byteData = encoding.GetBytes(data);

            try
            {
                if (async)
                {
                    // Begin sending the data to the remote device.
                    handler.BeginSend(byteData, 0, byteData.Length, SocketFlags.None, SendCallback,
                        new SendAsyncState {Payload = data, Socket = handler});
                }
                else // Block
                {
                    var bytesSent = handler.Send(byteData, 0, byteData.Length, SocketFlags.None);

                    if (ShowBytes && ShowData)
                        Console.WriteLine("{0}:{1} >>> {2} bytes: {3}", ((IPEndPoint)handler.RemoteEndPoint).Address, ((IPEndPoint)handler.RemoteEndPoint).Port, bytesSent, data.TrimEnd('\r', '\n'));
                    else if (ShowBytes)
                        Console.WriteLine("{0}:{1} >>> {2} bytes", ((IPEndPoint)handler.RemoteEndPoint).Address, ((IPEndPoint)handler.RemoteEndPoint).Port, bytesSent);
                    else if (ShowData)
                        Console.WriteLine("{0}:{1} >>> {2}", ((IPEndPoint)handler.RemoteEndPoint).Address, ((IPEndPoint)handler.RemoteEndPoint).Port, data.TrimEnd('\r', '\n'));
                }
            }
            catch (SocketException)
            {
                // Don't send 403 - the sending socket isn't working.
                Console.WriteLine("{0}:{1} XXX CONNECTION TERMINATED", ((IPEndPoint)handler.RemoteEndPoint).Address, ((IPEndPoint)handler.RemoteEndPoint).Port);
            }
        }
        private void SendCallback(IAsyncResult ar)
        {
            try
            {
                // Retrieve the socket from the state object.
                var handler = (SendAsyncState)ar.AsyncState;

                // Complete sending the data to the remote device.
                var bytesSent = handler.Socket.EndSend(ar);

                if (ShowBytes && ShowData)
                    Console.WriteLine("{0}:{1} >>> {2} bytes: {3}", ((IPEndPoint)handler.Socket.RemoteEndPoint).Address, ((IPEndPoint)handler.Socket.RemoteEndPoint).Port, bytesSent, handler.Payload.TrimEnd('\r', '\n'));
                else if (ShowBytes)
                    Console.WriteLine("{0}:{1} >>> {2} bytes", ((IPEndPoint)handler.Socket.RemoteEndPoint).Address, ((IPEndPoint)handler.Socket.RemoteEndPoint).Port, bytesSent);
                else if (ShowData)
                    Console.WriteLine("{0}:{1} >>> {2}", ((IPEndPoint)handler.Socket.RemoteEndPoint).Address, ((IPEndPoint)handler.Socket.RemoteEndPoint).Port, handler.Payload.TrimEnd('\r','\n'));
            }
            catch (ObjectDisposedException)
            {
                // Don't send 403 - the sending socket isn't working
            }
            catch (Exception e)
            {
                // Don't send 403 - the sending socket isn't working
                Console.WriteLine(e.ToString());
                throw;
            }
        }
        #endregion

        #region Database
        private static ISession OpenSession()
        {
            lock (_sessionFactoryLock)
            {
                if (_sessionFactory == null)
                {
                    var configuration = new Configuration();
                    configuration.AddAssembly(typeof(Newsgroup).Assembly);
                    _sessionFactory = configuration.BuildSessionFactory();
                }
            }
            return _sessionFactory.OpenSession();
        }
        #endregion

        #region NNTP Commands
        private CommandProcessingResult Article(Connection connection, string content)
        {
            var param = (string.Compare(content, "ARTICLE\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!connection.CurrentArticleNumber.HasValue)
                {
                    Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else if (string.IsNullOrEmpty(connection.CurrentNewsgroup) && !param.StartsWith("<", StringComparison.Ordinal))
            {
                Send(connection.WorkSocket, "412 No newsgroup selected\r\n");
                return new CommandProcessingResult(true);
            }
            
            using (var session = OpenSession())
            {
                Article article;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == connection.CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.MessageId == param);
                    type = 1;
                }
                else
                {
                    int articleId;
                    if (!int.TryParse(param, out articleId))
                    {
                        Send(connection.WorkSocket, "423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == articleId);
                    type = 2;
                }

                if (article == null)
                    switch (type)
                    {
                        case 1:
                            Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                            break;
                        case 2:
                            Send(connection.WorkSocket, "423 No article with that number\r\n");
                            break;
                        case 3:
                            Send(connection.WorkSocket, "420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    lock (connection.SendLock)
                    {
                        switch (type)
                        {
                            case 1:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "220 {0} {1} Article follows (multi-line)\r\n",
                                    (!string.IsNullOrEmpty(connection.CurrentNewsgroup) && string.CompareOrdinal(article.Newsgroup.Name, connection.CurrentNewsgroup) == 0) ? article.Id : 0,
                                    article.MessageId), false, Encoding.UTF8);
                                break;
                            case 2:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "220 {0} {1} Article follows (multi-line)\r\n", article.Id, article.MessageId), false, Encoding.UTF8);
                                break;
                            case 3:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "220 {0} {1} Article follows (multi-line)\r\n", article.Id, article.MessageId), false, Encoding.UTF8);
                                break;
                        }

                        Send(connection.WorkSocket, article.Headers + "\r\n", false, Encoding.UTF8);
                        Send(connection.WorkSocket, article.Body + "\r\n.\r\n", false, Encoding.UTF8);
                    }
                }
            }

            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Body(Connection connection, string content)
        {
            var param = (string.Compare(content, "BODY\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!connection.CurrentArticleNumber.HasValue)
                {
                    Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(connection.CurrentNewsgroup))
                {
                    Send(connection.WorkSocket, "412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = OpenSession())
            {
                int type;
                Article article;
                if (string.IsNullOrEmpty(param))
                {
                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == connection.CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    article = session.Query<Article>().Single(a => a.MessageId == param);
                    type = 1;
                }
                else
                {
                    int articleId;
                    if (!int.TryParse(param, out articleId))
                    {
                        Send(connection.WorkSocket, "423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == articleId);
                    type = 2;
                }

                if (article == null)
                    switch (type)
                    {
                        case 1:
                            Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                            break;
                        case 2:
                            Send(connection.WorkSocket, "423 No article with that number\r\n");
                            break;
                        case 3:
                            Send(connection.WorkSocket, "420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    lock (connection.SendLock)
                    {
                        switch (type)
                        {
                            case 1:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "222 {0} {1} Body follows (multi-line)\r\n",
                                    (!string.IsNullOrEmpty(connection.CurrentNewsgroup) && string.CompareOrdinal(article.Newsgroup.Name, connection.CurrentNewsgroup) == 0) ? article.Id : 0,
                                    article.MessageId));
                                break;
                            case 2:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "222 {0} {1} Body follows (multi-line)\r\n", article.Id, article.MessageId));
                                break;
                            case 3:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "222 {0} {1} Body follows (multi-line)\r\n", article.Id, article.MessageId));
                                break;
                        }

                        Send(connection.WorkSocket, article.Body, false, Encoding.UTF8);
                    }
                }
            }

            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Capabilities(Connection connection)
        {
            var sb = new StringBuilder();
            sb.Append("101 Capability list:\r\n");
            sb.Append("VERSION 2\r\n");
            sb.Append("IHAVE\r\n");
            sb.Append("HDR\r\n");
            sb.Append("LIST ACTIVE NEWSGROUPS\r\n");
            sb.Append("NEWNEWS\r\n");
            sb.Append("OVER\r\n");
            sb.Append("POST\r\n");
            sb.Append("READER\r\n");
            sb.Append("IMPLEMENTATION McNNTP 1.0.0\r\n");
            sb.Append(".\r\n");
            Send(connection.WorkSocket, sb.ToString());
            return new CommandProcessingResult(true);
        }

        internal static void ConsoleCreateAdministrator(string name, SecureString password)
        {
            var saltBytes = new byte[64];
            var rng = RandomNumberGenerator.Create();
            rng.GetNonZeroBytes(saltBytes);
            var salt = Convert.ToBase64String(saltBytes);

            var bstr = Marshal.SecureStringToBSTR(password);
            try
            {
                using (var session = OpenSession())
                {
                    session.Save(new Administrator
                    {
                        Username = name,
                        PasswordHash = Convert.ToBase64String(new SHA512CryptoServiceProvider().ComputeHash(Encoding.UTF8.GetBytes(string.Concat(salt, Marshal.PtrToStringBSTR(bstr))))),
                        PasswordSalt = salt,
                        CanCancel = true,
                        CanCheckGroups = true,
                        CanCreateGroup = true,
                        CanDeleteGroup = true
                    });
                    session.Close();
                }
            }
            finally
            {
                Marshal.FreeBSTR(bstr);
            }
        }

        internal static void ConsoleCreateGroup(string name, string desc)
        {
            using (var session = OpenSession())
            {
                session.Save(new Newsgroup
                {
                    Name = name,
                    Description = desc,
                    CreateDate = DateTime.UtcNow
                });
                session.Close();
            }
        }

        private CommandProcessingResult Date(Connection connection)
        {
            Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "111 {0:yyyyMMddHHmmss}\r\n", DateTime.UtcNow));
            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Group(Connection connection, string content)
        {
            content = content.TrimEnd('\r', '\n').Substring(content.IndexOf(' ') + 1).Split(' ')[0];
            Newsgroup ng;
            using (var session = OpenSession())
            {
                ng = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == content);
            }

            if (ng == null)
                Send(connection.WorkSocket, string.Format("411 {0} is unknown\r\n", content));
            else
            {
                connection.CurrentNewsgroup = ng.Name;
                connection.CurrentArticleNumber = ng.LowWatermark;

// ReSharper disable ConvertIfStatementToConditionalTernaryExpression
                if (ng.PostCount == 0)
// ReSharper restore ConvertIfStatementToConditionalTernaryExpression
                    Send(connection.WorkSocket, string.Format("211 0 0 0 {0}\r\n", ng.Name));
                else
                    Send(connection.WorkSocket, string.Format("211 {0} {1} {2} {3}\r\n", ng.PostCount, ng.LowWatermark, ng.HighWatermark, ng.Name));
            }
            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Hdr(Connection connection, string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');
            if (parts.Length < 2 || parts.Length > 3)
            {
                Send(connection.WorkSocket, "501 Syntax Error\r\n");
                return new CommandProcessingResult(true);
            }
            
            int type;

            if (parts.Length == 3 && parts[2].StartsWith("<", StringComparison.Ordinal))
                type = 1;
            else if (parts.Length == 3 && !parts[2].StartsWith("<", StringComparison.Ordinal))
            {
                type = 2;
                int articleId;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out articleId))
                {
                    Send(connection.WorkSocket, "501 Syntax Error\r\n");
                    return new CommandProcessingResult(true);
                }

                if (string.IsNullOrEmpty(connection.CurrentNewsgroup))
                {
                    Send(connection.WorkSocket, "412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else //if (parts.Length == 2)
            {
                type = 3;
                if (string.IsNullOrEmpty(connection.CurrentNewsgroup))
                {
                    Send(connection.WorkSocket, "412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
                if (!connection.CurrentArticleNumber.HasValue)
                {
                    Send(connection.WorkSocket, "420 Current article number is invalid\r\n");
                    return new CommandProcessingResult(true);
                }
            }

            using (var session = OpenSession())
            {
                IEnumerable<Article> articles;
                switch (type)
                {
                    case 1:
                        articles = new[] { session.Query<Article>().SingleOrDefault(a => a.MessageId == parts[2]) };
                        break;
                    case 2:
                        var range = ParseRange(parts[2]);
                        if (range.Equals(default(System.Tuple<int, int?>)))
                        {
                            Send(connection.WorkSocket, "501 Syntax Error\r\n");
                            return new CommandProcessingResult(true);
                        }

                        articles = (range.Item2.HasValue)
                            ? session.Query<Article>().Fetch(a => a.Newsgroup).Where(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id >= range.Item1 && a.Id <= range.Item2)
                            : session.Query<Article>().Fetch(a => a.Newsgroup).Where(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id >= range.Item1);
                        break;
                    case 3:
                        Debug.Assert(connection.CurrentArticleNumber.HasValue);
                        articles = session.Query<Article>().Fetch(a => a.Newsgroup).Where(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == connection.CurrentArticleNumber.Value);
                        break;
                    default:
                        // Unrecognized...
                        Send(connection.WorkSocket, "501 Syntax Error\r\n");
                        return new CommandProcessingResult(true);
                }

                if (!articles.Any())
                    switch (type)
                    {
                        case 1:
                            Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                            break;
                        case 2:
                            Send(connection.WorkSocket, "423 No articles in that range\r\n");
                            break;
                        case 3:
                            Send(connection.WorkSocket, "420 Current article number is invalid\r\n");
                            break;
                    }
                else
                {
                    lock (connection.SendLock)
                    {
                        Send(connection.WorkSocket, "225 Headers follow (multi-line)\r\n");

                        Func<Article, string> headerFunction;
                        switch (parts[1].ToUpperInvariant())
                        {
                            case "DATE":
                                headerFunction = a => a.Date;
                                break;
                            case "FROM":
                                headerFunction = a => a.From;
                                break;
                            case "MESSAGE-ID":
                                headerFunction = a => a.MessageId;
                                break;
                            case "REFERENCES":
                                headerFunction = a => a.References;
                                break;
                            case "SUBJECT":
                                headerFunction = a => a.Subject;
                                break;
                            default:
                            {
                                Dictionary<string, string> headers;
                                headerFunction = a => Data.Article.TryParseHeaders(a.Headers, out headers) 
                                    ? headers.Any(h => string.Compare(h.Key, parts[1], StringComparison.OrdinalIgnoreCase) == 0)
                                        ? headers.Single(h => string.Compare(h.Key, parts[1], StringComparison.OrdinalIgnoreCase) == 0).Value
                                        : null
                                    : null;
                                break;
                            }
                        }

                        foreach (var article in articles)
                            if (type == 1)
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "{0} {1}\r\n",
                                    (!string.IsNullOrEmpty(connection.CurrentNewsgroup) && string.CompareOrdinal(article.Newsgroup.Name, connection.CurrentNewsgroup) == 0) ? article.MessageId : "0",
                                    headerFunction.Invoke(article)), false, Encoding.UTF8);
                            else
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "{0} {1}\r\n",
                                    article.Id,
                                    headerFunction.Invoke(article)), false, Encoding.UTF8);

                        Send(connection.WorkSocket, ".\r\n");
                    }
                }
            }

            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Head(Connection connection, string content)
        {
            var param = (string.Compare(content, "HEAD\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!connection.CurrentArticleNumber.HasValue)
                {
                    Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(connection.CurrentNewsgroup))
                {
                    Send(connection.WorkSocket, "412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            
            using (var session = OpenSession())
            {
                Article article;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == connection.CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    article = session.Query<Article>().FirstOrDefault(a => a.MessageId == param);
                    type = 1;
                }
                else
                {
                    int articleId;
                    if (!int.TryParse(param, out articleId))
                    {
                        Send(connection.WorkSocket, "423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == articleId);
                    type = 2;
                }

                if (article == null)
                    switch (type)
                    {
                        case 1:
                            Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                            break;
                        case 2:
                            Send(connection.WorkSocket, "423 No article with that number\r\n");
                            break;
                        case 3:
                            Send(connection.WorkSocket, "420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    lock (connection.SendLock)
                    {
                        switch (type)
                        {
                            case 1:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "221 {0} {1} Headers follow (multi-line)\r\n",
                                    (string.CompareOrdinal(article.Newsgroup.Name, connection.CurrentNewsgroup) == 0) ? article.Id : 0, article.MessageId));
                                break;
                            case 2:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "221 {0} {1} Headers follow (multi-line)\r\n", article.Id, article.MessageId));
                                break;
                            case 3:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "221 {0} {1} Headers follow (multi-line)\r\n", article.Id, article.MessageId));
                                break;
                        }

                        Send(connection.WorkSocket, article.Headers + "\r\n.\r\n", false, Encoding.UTF8);
                    }
                }
            }

            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Help(Connection connection)
        {
            var sb = new StringBuilder();
            sb.Append("100 Help text follows\r\n");

            var dirName = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dirName != null && File.Exists(Path.Combine(dirName, "HelpFile.txt")))
            {
                using (var sr = new StreamReader(Path.Combine(dirName, "HelpFile.txt"), Encoding.UTF8))
                {
                    sb.Append(sr.ReadToEnd());
                    sr.Close();
                }
            }
            else
            {
                sb.Append("The list of commands understood by this server are:\r\n");
                foreach (var cmd in _commandDirectory)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0}\r\n", cmd.Key);
            }

            if (!sb.ToString().EndsWith("\r\n.\r\n"))
                sb.Append("\r\n.\r\n");
            
            Send(connection.WorkSocket, sb.ToString());
            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult List(Connection connection, string content)
        {
            if (string.Compare(content, "LIST\r\n", StringComparison.OrdinalIgnoreCase) == 0 ||
                string.Compare(content, "LIST ACTIVE\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                IList<Newsgroup> newsGroups;

                try
                {
                    using (var session = OpenSession())
                    {
                        newsGroups = session.Query<Newsgroup>().OrderBy(n => n.Name).ToList();
                    }
                }
                catch (MappingException mex)
                {
                    Console.WriteLine(mex.ToString());
                    Send(connection.WorkSocket, "403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }
                catch (Exception)
                {
                    Send(connection.WorkSocket, "403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                lock (connection.SendLock)
                {
                    Send(connection.WorkSocket, "215 list of newsgroups follows\r\n");
                    foreach (var ng in newsGroups)
                        Send(connection.WorkSocket, string.Format("{0} {1} {2} {3}\r\n", ng.Name, ng.HighWatermark, ng.LowWatermark, connection.CanPost ? "y" : "n"), false, Encoding.UTF8);
                    Send(connection.WorkSocket, ".\r\n");
                }
                return new CommandProcessingResult(true);
            }

            if (string.Compare(content, "LIST NEWSGROUPS\r\n", StringComparison.OrdinalIgnoreCase) == 0)
            {
                IList<Newsgroup> newsGroups;

                try
                {
                    using (var session = OpenSession())
                    {
                        newsGroups = session.Query<Newsgroup>().OrderBy(n => n.Name).ToList();
                    }
                }
                catch (MappingException mex)
                {
                    Console.WriteLine(mex.ToString());
                    Send(connection.WorkSocket, "403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }
                catch (Exception)
                {
                    Send(connection.WorkSocket, "403 Archive server temporarily offline\r\n");
                    return new CommandProcessingResult(true);
                }

                lock (connection.SendLock)
                {
                    Send(connection.WorkSocket, "215 information follows\r\n");
                    foreach (var ng in newsGroups)
                        Send(connection.WorkSocket, string.Format("{0}\t{1}\r\n", ng.Name, ng.Description), false, Encoding.UTF8);
                    Send(connection.WorkSocket, ".\r\n");
                }
                return new CommandProcessingResult(true);
            }

            Send(connection.WorkSocket, "501 Syntax Error\r\n");
            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Mode(Connection connection, string content)
        {
            if (content.StartsWith("MODE READER", StringComparison.OrdinalIgnoreCase))
            {
                Send(connection.WorkSocket, "200 This server is not a mode-switching server, but whatever!\r\n");
                return new CommandProcessingResult(true);
            }

            Send(connection.WorkSocket, "501 Syntax Error\r\n");
            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Newgroups(Connection connection, string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');

            var dateTime = string.Join(" ", parts.ElementAt(1), parts.ElementAt(2));
            DateTime afterDate;
            if (!(parts.ElementAt(1).Length == 8 && DateTime.TryParseExact(dateTime, "yyyyMMdd HHmmss", CultureInfo.InvariantCulture, parts.Length == 4 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out afterDate)))
                if (!(parts.ElementAt(1).Length == 6 && DateTime.TryParseExact(dateTime, "yyMMdd HHmmss", CultureInfo.InvariantCulture, parts.Length == 4 ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out afterDate)))
                    afterDate = DateTime.MinValue;

            if (afterDate != DateTime.MinValue)
            {
                IList<Newsgroup> newsGroups;
                using (var session = OpenSession())
                {
                    newsGroups = session.Query<Newsgroup>().Where(n => n.CreateDate >= afterDate).OrderBy(n => n.Name).ToList();
                }

                lock (connection.SendLock)
                {
                    Send(connection.WorkSocket, "231 List of new newsgroups follows (multi-line)\r\n", false, Encoding.UTF8);
                    foreach (var ng in newsGroups)
                        Send(connection.WorkSocket, string.Format("{0} {1} {2} {3}\r\n", ng.Name, ng.HighWatermark, ng.LowWatermark,
                            connection.CanPost ? "y" : "n"), false, Encoding.UTF8);
                    Send(connection.WorkSocket, ".\r\n", false, Encoding.UTF8);
                }
            }
            else
            {
                lock (connection.SendLock)
                {
                    Send(connection.WorkSocket, "231 List of new newsgroups follows (multi-line)\r\n");
                    Send(connection.WorkSocket, ".\r\n");
                }
            }

            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult Quit(Connection connection)
        {
            lock (connection.SendLock)
            {
                Send(connection.WorkSocket, "205 closing connection\r\n", false, Encoding.UTF8); // Block.
                if (connection.WorkSocket != null)
                {
                    connection.WorkSocket.Shutdown(SocketShutdown.Both);
                    connection.WorkSocket.Close();
                }
            }
            return new CommandProcessingResult(true, true);
        }
        private CommandProcessingResult ListGroup(Connection connection, string content)
        {
            var parts = content.TrimEnd('\r', '\n').Split(' ');

            if (parts.Length == 1 && connection.CurrentNewsgroup == null)
                Send(connection.WorkSocket, "412 No newsgroup selected\r\n");

            using (var session = OpenSession())
            {
                var name = (parts.Length == 2) ? parts[1] : connection.CurrentNewsgroup;
                var ng = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == name);

                if (ng == null)
                    Send(connection.WorkSocket, "411 No such newsgroup\r\n");
                else
                {
                    connection.CurrentNewsgroup = ng.Name;
                    if (ng.PostCount == 0)
                    {
                        lock (connection.SendLock)
                        {
                            Send(connection.WorkSocket, string.Format("211 0 0 0 {0}\r\n", ng.Name));
                        }
                    }
                    else
                    {
                        IList<Article> articles;
                        if (parts.Length < 3)
                            articles = session.Query<Article>().Fetch(a => a.Newsgroup).Where(a => a.Newsgroup.Name == ng.Name).OrderBy(a => a.Id).ToList();
                        else
                        {
                            var range = ParseRange(parts[2]);
                            if (range.Equals(default(System.Tuple<int, int?>)))
                            {
                                Send(connection.WorkSocket, "501 Syntax Error\r\n");
                                return new CommandProcessingResult(true);
                            }

                            if (!range.Item2.HasValue) // LOW-
                                articles = session.Query<Article>().Fetch(a => a.Newsgroup).Where(a => a.Newsgroup.Name == ng.Name && a.Id >= range.Item1).OrderBy(a => a.Id).ToList();
                            else // LOW-HIGH
                                articles = session.Query<Article>().Fetch(a => a.Newsgroup).Where(a => a.Newsgroup.Name == ng.Name && a.Id >= range.Item1 && a.Id <= range.Item2.Value).ToList();
                        }

                        connection.CurrentArticleNumber = !articles.Any() ? default(long?) : articles.First().Id;

                        lock (connection.SendLock)
                        {
                            Send(connection.WorkSocket, string.Format("211 {0} {1} {2} {3}\r\n", ng.PostCount, ng.LowWatermark, ng.HighWatermark, ng.Name), false, Encoding.UTF8);
                            foreach (var article in articles)
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "{0}\r\n", article.Id.ToString(CultureInfo.InvariantCulture)), false, Encoding.UTF8);
                            Send(connection.WorkSocket, ".\r\n", false, Encoding.UTF8);
                        }
                    }
                }
            }


            return new CommandProcessingResult(true);
        }

        private CommandProcessingResult Post(Connection connection, string content)
        {
            if (!connection.CanPost)
            {
                Send(connection.WorkSocket, "440 Posting not permitted\r\n");
                return new CommandProcessingResult(true);
            }

            Send(connection.WorkSocket, "340 Send article to be posted\r\n");

            Func<Connection, string, CommandProcessingResult, CommandProcessingResult> messageAccumulator = null;
            messageAccumulator = (conn, msg, prev) =>
            {
                if (msg != null && (msg.EndsWith("\r\n.\r\n") || (prev.Message.EndsWith("\r\n") && msg.EndsWith(".\r\n"))))
                {
                    try
                    {
                        Article article;
                        if (!Data.Article.TryParse(prev.Message == null ? msg.Substring(0, msg.Length - 5) : prev.Message + msg, false, out article))
                            Send(connection.WorkSocket, "441 Posting failed\r\n");
                        else
                        {
                            using (var session = OpenSession())
                            {
                                foreach (var newsgroupName in article.Newsgroups.Split(' '))
                                {
                                    var newsgroupNameClosure = newsgroupName;
                                    var newsgroup = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == newsgroupNameClosure);
                                    if (newsgroup == null)
                                        continue;

                                    article.Id = 0;
                                    article.Newsgroup = newsgroup;
                                    article.Path = ServerPath;
                                    session.Save(article);
                                }

                                session.Close();
                            }
                        }

                        Send(connection.WorkSocket, "240 Article received OK\r\n");

                        return new CommandProcessingResult(true, true)
                        {
                            Message = prev.Message + msg
                        };
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                        Send(connection.WorkSocket, "441 Posting failed\r\n");
                    }
                }

                return new CommandProcessingResult(true, false)
                {
                    MessageHandler = messageAccumulator,
                    Message = prev == null ? msg : prev.Message == null ? msg : prev.Message + "\r\n" + msg
                };
            };

            return messageAccumulator.Invoke(connection, null, null);
        }

        private CommandProcessingResult Stat(Connection connection, string content)
        {
            var param = (string.Compare(content, "STAT\r\n", StringComparison.OrdinalIgnoreCase) == 0)
                ? null
                : content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (string.IsNullOrEmpty(param))
            {
                if (!connection.CurrentArticleNumber.HasValue)
                {
                    Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(connection.CurrentNewsgroup))
                {
                    Send(connection.WorkSocket, "412 No newsgroup selected\r\n");
                    return new CommandProcessingResult(true);
                }
            }
            
            using (var session = OpenSession())
            {
                Article article;
                int type;
                if (string.IsNullOrEmpty(param))
                {
                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == connection.CurrentArticleNumber);
                    type = 3;
                }
                else if (param.StartsWith("<", StringComparison.Ordinal))
                {
                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.MessageId == param);
                    type = 1;
                }
                else
                {
                    int articleId;
                    if (!int.TryParse(param, out articleId))
                    {
                        Send(connection.WorkSocket, "423 No article with that number\r\n");
                        return new CommandProcessingResult(true);
                    }

                    article = session.Query<Article>().Fetch(a => a.Newsgroup).SingleOrDefault(a => a.Newsgroup.Name == connection.CurrentNewsgroup && a.Id == articleId);
                    type = 2;
                }

                if (article == null)
                    switch (type)
                    {
                        case 1:
                            Send(connection.WorkSocket, "430 No article with that message-id\r\n");
                            break;
                        case 2:
                            Send(connection.WorkSocket, "423 No article with that number\r\n");
                            break;
                        case 3:
                            Send(connection.WorkSocket, "420 Current article number is invalid\r\n");
                            break;

                    }
                else
                {
                    lock (connection.SendLock)
                    {
                        switch (type)
                        {
                            case 1:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "223 {0} {1}\r\n",
                                    (!string.IsNullOrEmpty(connection.CurrentNewsgroup) && string.CompareOrdinal(article.Newsgroup.Name, connection.CurrentNewsgroup) == 0) ? article.Id : 0,
                                    article.MessageId));
                                break;
                            case 2:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "223 {0} {1}\r\n", article.Id, article.MessageId));
                                break;
                            case 3:
                                Send(connection.WorkSocket, string.Format(CultureInfo.InvariantCulture, "223 {0} {1}\r\n", article.Id, article.MessageId));
                                break;
                        }
                    }
                }
            }

            return new CommandProcessingResult(true);
        }
        private CommandProcessingResult XOver(Connection connection, string content)
        {
            var rangeExpression = content.Substring(content.IndexOf(' ') + 1).TrimEnd('\r', '\n');

            if (connection.CurrentNewsgroup == null)
                Send(connection.WorkSocket, "412 No news group current selected\r\n");
            else
            {
                Newsgroup ng;
                IList<Article> articles;

                try
                {
                    using (var session = OpenSession())
                    {
                        ng = session.Query<Newsgroup>().SingleOrDefault(n => n.Name == connection.CurrentNewsgroup);

                        if (string.IsNullOrEmpty(rangeExpression))
                            articles =
                                session.Query<Article>()
                                    .Fetch(a => a.Newsgroup)
                                    .Where(a => a.Newsgroup.Name == ng.Name)
                                    .OrderBy(a => a.Id)
                                    .ToList();
                        else
                        {
                            var range = ParseRange(rangeExpression);
                            if (range.Equals(default(System.Tuple<int, int?>)))
                            {
                                Send(connection.WorkSocket, "501 Syntax Error\r\n");
                                return new CommandProcessingResult(true);
                            }

                            if (!range.Item2.HasValue) // LOW-
                            {
                                articles =
                                    session.Query<Article>()
                                        .Fetch(a => a.Newsgroup)
                                        .Where(a => a.Newsgroup.Name == ng.Name && a.Id >= range.Item1)
                                        .OrderBy(a => a.Id)
                                        .ToList();
                            }
                            else // LOW-HIGH
                            {
                                articles =
                                    session.Query<Article>()
                                        .Fetch(a => a.Newsgroup)
                                        .Where(a => a.Newsgroup.Name == ng.Name && a.Id >= range.Item1 && a.Id <= range.Item2.Value)
                                        .OrderBy(a => a.Id)
                                        .ToList();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Send(connection.WorkSocket, "403 Archive server temporarily offline\r\n");
                    Console.WriteLine(ex.ToString());
                    return new CommandProcessingResult(true);
                }

                if (ng == null)
                {
                    Send(connection.WorkSocket, "411 No such newsgroup\r\n");
                    return new CommandProcessingResult(true);
                }

                if (!articles.Any())
                {
                    Send(connection.WorkSocket, "420 No article(s) selected\r\n");
                    return new CommandProcessingResult(true);
                }

                connection.CurrentArticleNumber = articles.First().Id;
                Func<string, string> unfold = i => string.IsNullOrWhiteSpace(i) ? i : i.Replace("\r\n", "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

                lock (connection.SendLock)
                {
                    Send(connection.WorkSocket, "224 Overview information follows\r\n", false, Encoding.UTF8);
                    foreach (var article in articles)
                        Send(connection.WorkSocket,
                            string.Format(CultureInfo.InvariantCulture,
                                "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\r\n",
                                string.CompareOrdinal(article.Newsgroup.Name, connection.CurrentNewsgroup) == 0 ? article.Id : 0,
                                unfold(article.Subject),
                                unfold(article.From),
                                unfold(article.Date),
                                unfold(article.MessageId),
                                unfold(article.References),
                                unfold((article.Body.Length*2).ToString(CultureInfo.InvariantCulture)),
                                unfold(article.Body.Split(new[] {"\r\n"}, StringSplitOptions.None).Length.ToString(CultureInfo.InvariantCulture))), false,
                            Encoding.UTF8);
                    Send(connection.WorkSocket, ".\r\n", false, Encoding.UTF8);
                }
            }

            return new CommandProcessingResult(true);
        }
        #endregion

        #region Interactivity
        public bool ShowBytes { get; set; }
        public bool ShowCommands { get; set; }
        public bool ShowData { get; set; }

        internal Dictionary<IPEndPoint, string> GetAllBuffs()
        {
        again:
            foreach (var conn in _connections)
                lock (conn.SendLock)
                {
                    try
                    {
                        if (conn.WorkSocket != null && !conn.WorkSocket.Connected)
                        {
                            _connections.Remove(conn);
                            conn.WorkSocket.Shutdown(SocketShutdown.Both);
                            conn.WorkSocket.Dispose();
                            conn.WorkSocket = null;
                            goto again;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        conn.WorkSocket = null;
                        _connections.Remove(conn);
                        goto again;
                    }
                    catch (SocketException)
                    {
                        conn.WorkSocket = null;
                        _connections.Remove(conn);
                        goto again;
                    }
                }

            // ReSharper disable once PossibleNullReferenceException
            return _connections.Where(c => c.WorkSocket != null).ToDictionary(conn => (IPEndPoint) conn.WorkSocket.RemoteEndPoint, conn => conn.Builder.ToString());
        }
        #endregion
        private static System.Tuple<int, int?> ParseRange(string input)
        {
            int low, high;
            if (input.IndexOf('-') == -1)
            {
                if (!int.TryParse(input, out low))
                    return default(System.Tuple<int, int?>);
                return new System.Tuple<int, int?>(low, low);
            }
            if (input.EndsWith("-", StringComparison.Ordinal))
            {
                if (!int.TryParse(input, out low))
                    return default(System.Tuple<int, int?>);
                return new System.Tuple<int, int?>(low, null);
            }

            if (!int.TryParse(input.Substring(0, input.IndexOf('-')), NumberStyles.Integer, CultureInfo.InvariantCulture, out low))
                return default(System.Tuple<int, int?>);
            if (!int.TryParse(input.Substring(input.IndexOf('-') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out high))
                return default(System.Tuple<int, int?>);

            return new System.Tuple<int, int?>(low, high);
        }

        public void InitializeDatabase()
        {
            var configuration = new Configuration();
            configuration.AddAssembly(typeof(Newsgroup).Assembly);
            configuration.Configure();

            using (var connection = new SQLiteConnection(configuration.GetProperty("connection.connection_string")))
            {
                connection.Open();
                try
                {
                    var update = new SchemaUpdate(configuration);
                    update.Execute(false, true);

                    // Update failed..  recreate it.
                    if (!VerifyDatabase())
                    {
                        var export = new SchemaExport(configuration);
                        export.Execute(false, true, false, connection, null);

                        using (var session = OpenSession())
                        {
                            session.Save(new Newsgroup
                            {
                                CreateDate = DateTime.UtcNow,
                                Description = "Control group for the repository",
                                Name = "freenews.config"
                            });
                            session.Close();
                        }
                    }
                }
                finally
                {
                    connection.Close();
                }

            }
        }

        public bool VerifyDatabase()
        {
            try
            {
                using (var session = OpenSession())
                {
                    var newsgroupCount = session.Query<Newsgroup>().Count(n => n.Name != null);
                    Console.WriteLine("Verified database has {0} newsgroups", newsgroupCount);

                    var articleCount = session.Query<Article>().Count(a => a.Headers != null);
                    Console.WriteLine("Verified database has {0} articles", articleCount);

                    var adminCount = session.Query<Administrator>().Count(a => a.CanCancel);
                    Console.WriteLine("Verified database has {0} local admins", adminCount);

                    session.Close();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}