﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SignalR.Infrastructure;

namespace SignalR
{
    public class Connection : IConnection
    {
        private readonly Signaler _signaler;
        private readonly IMessageStore _store;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly string _baseSignal;
        private readonly string _clientId;
        private readonly HashSet<string> _signals;
        private readonly HashSet<string> _groups;
        private readonly object _lockObj = new object();

        public Connection(IMessageStore store,
                          IJsonSerializer jsonSerializer,
                          Signaler signaler,
                          string baseSignal,
                          string clientId,
                          IEnumerable<string> signals)
            : this(store,
                   jsonSerializer,
                   signaler,
                   baseSignal,
                   clientId,
                   signals,
                   Enumerable.Empty<string>())
        {
        }

        public Connection(IMessageStore store,
                          IJsonSerializer jsonSerializer,
                          Signaler signaler,
                          string baseSignal,
                          string clientId,
                          IEnumerable<string> signals,
                          IEnumerable<string> groups)
        {
            _store = store;
            _jsonSerializer = jsonSerializer;
            _signaler = signaler;
            _baseSignal = baseSignal;
            _clientId = clientId;
            _signals = new HashSet<string>(signals);
            _groups = new HashSet<string>(groups);
        }

        public TimeSpan ReceiveTimeout
        {
            get
            {
                return _signaler.DefaultTimeout;
            }
            set
            {
                _signaler.DefaultTimeout = value;
            }
        }

        private IEnumerable<string> Signals
        {
            get
            {
                return _signals.Concat(_groups);
            }
        }

        public virtual Task Broadcast(object value)
        {
            return Broadcast(_baseSignal, value);
        }

        public virtual Task Broadcast(string message, object value)
        {
            return SendMessage(message, value);
        }

        public Task Send(object value)
        {
            return SendMessage(_clientId, value);
        }

        public Task<PersistentResponse> ReceiveAsync()
        {
            // Get the last message id then wait for new messages to arrive
            return _store.GetLastId()
                         .ContinueWith(storeTask => WaitForSignal(storeTask.Result))
                         .Unwrap();
        }

        public Task<PersistentResponse> ReceiveAsync(long messageId)
        {
            // Get all messages for this message id, or wait until new messages if there are none
            return GetResponse(messageId).ContinueWith(task => ProcessReceive(task, messageId))
                                         .Unwrap();
        }

        public static IConnection GetConnection<T>() where T : PersistentConnection
        {
            return GetConnection(typeof(T).FullName);
        }

        public static IConnection GetConnection(string connectionType)
        {
            return new Connection(DependencyResolver.Resolve<IMessageStore>(),
                                  DependencyResolver.Resolve<IJsonSerializer>(),
                                  Signaler.Instance,
                                  connectionType,
                                  null,
                                  new[] { connectionType });
        }

        private Task<PersistentResponse> ProcessReceive(Task<PersistentResponse> responseTask, long? messageId = null)
        {
            // No messages to return so we need to subscribe until we have something
            if (responseTask.Result == null)
            {
                return WaitForSignal(messageId);
            }

            // Return the task as is
            return responseTask;
        }

        private Task<PersistentResponse> WaitForSignal(long? messageId = null)
        {
            // Wait for a signal to get triggered and return with a response
            return _signaler.Subscribe(Signals)
                            .ContinueWith(task => ProcessSignal(task, messageId))
                            .Unwrap();
        }

        private Task<PersistentResponse> ProcessSignal(Task<SignalResult> signalTask, long? messageId = null)
        {
            if (signalTask.Result.TimedOut)
            {
                PersistentResponse response = GetEmptyResponse(messageId);

                // Return a task wrapping the result
                return TaskAsyncHelper.FromResult(response);
            }

            // Get the response for this message id
            return GetResponse(messageId ?? 0).Success(task => task.Result ?? GetEmptyResponse(messageId));
        }

        private PersistentResponse GetEmptyResponse(long? messageId)
        {
            var response = new PersistentResponse
            {
                MessageId = messageId ?? 0
            };

            PopulateResponseState(response);

            return response;
        }

        private Task<PersistentResponse> GetResponse(long messageId)
        {
            // Get all messages for the current set of signals
            return GetMessages(messageId, Signals)
                .Success(messageTask =>
                {
                    var results = messageTask.Result;
                    if (!results.Any())
                    {
                        return null;
                    }

                    var response = new PersistentResponse();

                    var commands = results.Where(m => m.SignalKey.EndsWith(PersistentConnection.SignalrCommand));

                    ProcessCommands(commands);

                    messageId = results.Max(p => p.Id);

                    // Get the message values and the max message id we received
                    var messageValues = results.Except(commands)
                        .Select(m => m.Value)
                        .ToList();

                    response.MessageId = messageId;
                    response.Messages = messageValues;

                    PopulateResponseState(response);

                    return response;
                });
        }

        private void ProcessCommands(IEnumerable<Message> messages)
        {
            foreach (var message in messages)
            {
                var command = GetCommand(message);
                if (command == null)
                {
                    continue;
                }
                
                switch (command.Type)
                {
                    case CommandType.AddToGroup:
                        _groups.Add((string)command.Value);
                        break;
                    case CommandType.RemoveFromGroup:
                        _groups.Remove((string)command.Value);
                        break;
                }
            }
        }

        private SignalCommand GetCommand(Message message)
        {
            var command = message.Value as SignalCommand;

            // Optimization for in memory message store
            if (command != null)
            {
                return command;
            }

            // Otherwise deserialize the message value
            string value = message.Value as string;
            if (value == null)
            {
                return null;
            }

            return _jsonSerializer.Parse<SignalCommand>(value);
        }

        private Task SendMessage(string message, object value)
        {
            return _store.Save(message, value)
                         .Success(_ => _signaler.Signal(message))
                         .Unwrap()
                         .Catch();
        }

        private Task<List<Message>> GetMessages(long id, IEnumerable<string> signals)
        {
            //var pendingMessagesTasks = (from signal in signals
            //                            select _store.GetAllSince(signal, id)).ToArray();

            // If there are no pending messages, we need to shortcut since ContinueWhenAll
            // blows up for empty arrays.
            //if (!pendingMessagesTasks.Any())
            //{
            //    return TaskAsyncHelper.FromResult(Enumerable.Empty<Message>());
            //}

            // Wait until all of the tasks are done before we return
            //return pendingMessagesTasks.AllSucceeded(() => (IEnumerable<Message>)pendingMessagesTasks.SelectMany(t => t.Result).OrderBy(m => m.Id).ToList());


            return _store.GetAllSince(signals, id)
                .Success(task => task.Result.ToList());
        }

        private void PopulateResponseState(PersistentResponse response)
        {
            // Set the groups on the outgoing transport data
            response.TransportData["Groups"] = _groups;
        }
    }
}