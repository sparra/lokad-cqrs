using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Cqrs.Core.Envelope;
using Lokad.Cqrs.Core.Outbox;
using System.Linq;
using Lokad.Cqrs.Feature.StreamingStorage;

namespace Lokad.Cqrs.Feature.TimerService
{
    public sealed class FileTimerService : IEngineProcess
    {
        readonly IQueueWriter _target;
        readonly IStreamingContainer _storage;
        readonly string _suffix;
        readonly IEnvelopeStreamer _streamer;
        public FileTimerService(IQueueWriter target, IStreamingContainer storage, IEnvelopeStreamer streamer)
        {
            _target = target;
            _storage = storage;
            _streamer = streamer;
            _suffix = Guid.NewGuid().ToString().Substring(0, 4);
        }

        public void Dispose()
        {
        }

        public sealed class Record

        {
            public readonly DateTime DeliverOn;
            public readonly string Name;
            public Record(string name, DateTime deliverOn)
            {
                Name = name;
                DeliverOn = deliverOn;
            }
        }

        static long _universalCounter;
        readonly List<Record> _scheduler = new List<Record>();

        public void PutMessage(ImmutableEnvelope envelope)
        {
            if (envelope.DeliverOnUtc < DateTime.UtcNow)
            {
                _target.PutMessage(envelope);
                return;
            }
            // save to the store
            var id = Interlocked.Increment(ref _universalCounter);
            var s = "{0:yyyy-MM-dd-HH-mm-ss}-{1:00000000}-{2}.future";
            var fileName = string.Format(s, envelope.DeliverOnUtc, id, _suffix);

            // persist
            var item = _storage.GetItem(fileName);

            var data = _streamer.SaveEnvelopeData(envelope);

            item.Write(x => x.Write(data, 0, data.Length));

            // add to in-memory scheduler
            lock (_scheduler)
            {
                _scheduler.Add(new Record(fileName, envelope.DeliverOnUtc));
            }
        }

        public void Initialize()
        {
            _storage.Create();
            var messages = _storage.ListItems().Select(fi =>
                    {
                        var item = _storage.GetItem(fi);
                        try
                        {
                            using (var mem = new MemoryStream())
                            {
                                item.ReadInto((x,y) => y.CopyTo(mem));
                                var data = _streamer.ReadAsEnvelopeData(mem.ToArray());
                                return new Record(fi, data.DeliverOnUtc);
                            }

                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine(ex);
                            return null;
                        }

                    }).Where(n => null != n).ToList();
            lock (_scheduler)
            {
                _scheduler.AddRange(messages);
            }


        }

        public Task Start(CancellationToken token)
        {
            return Task.Factory.StartNew(() => RunScheduler(token), token);

        }

        void RunScheduler(CancellationToken token)
        {
            while(!token.IsCancellationRequested)
            {
                try
                {
                    var date = DateTime.UtcNow;
                    var count = 100;
                    List<Record> list;
                    lock (_scheduler)
                    {
                        list = _scheduler.Where(r => r.DeliverOn <= date).Take(count).ToList();
                    }
                    if (list.Count > 0)
                    {
                        foreach (var record in list)
                        {
                            var item = _storage.GetItem(record.Name);

                            ImmutableEnvelope e;
                            using (var mem = new MemoryStream())
                            {
                                item.ReadInto((x, y) => y.CopyTo(mem));
                                e = _streamer.ReadAsEnvelopeData(mem.ToArray());
                            }

                            // we need to reset the timer here.
                            var newEnvelope = EnvelopeBuilder.CloneProperties(e.EnvelopeId + "-future", e);
                            newEnvelope.DeliverOnUtc(DateTime.MinValue);
                            newEnvelope.AddString("original-id", e.EnvelopeId);
                            _target.PutMessage(newEnvelope.Build());
                            
                            item.Delete();
                            lock(_scheduler)
                            {
                                _scheduler.Remove(record);
                            }
                            
                        }
                    }
                    token.WaitHandle.WaitOne(5000);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex);
                    token.WaitHandle.WaitOne(2000);

                }
            }
        }
    }
}