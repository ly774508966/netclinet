using System.Net.Sockets;
using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Game {
	public class NetRemoteClose : ApplicationException {}
	public class NetIsClosed : ApplicationException {}
	public class NetIsWorking : ApplicationException {}
	public class NetIsConnecting : ApplicationException {}
	public class NetInvalidMessage : ApplicationException {}

	public class NetError {
		public readonly Exception exception;

		public NetError(Exception exception) {
			this.exception = exception;
		}

		public int getErrorCode() {
			return 0;
		}
	}

	public class Message {
		public const int HeadSize = 8;
		public const int MessageBufferSize = 1024 * 1024;

		public readonly int proto;
		public readonly byte[] message;

		public Message(int proto, byte[] message) {
			this.proto = proto;
			this.message = message;
		}

		public byte[] GetData() {
			var protoBytes = BitConverter.GetBytes((Int32)this.proto);
			var lengthBytes = BitConverter.GetBytes((Int32)this.message.Length);

			if (!BitConverter.IsLittleEndian) {
				protoBytes.Reverse();
				lengthBytes.Reverse();
			}

			var data = new byte[HeadSize + message.Length];
			protoBytes.CopyTo(data, 0);
			lengthBytes.CopyTo(data, sizeof(Int32));
			message.CopyTo(data, HeadSize);

			return data;
		}

		public static void DecodeHead(byte[] head, out int proto, out int length) {
			if (head.Length != HeadSize) {
				throw new NetInvalidMessage();
			}

			byte[] protoBytes = head.Take(4).ToArray();
			byte[] lengthBytes = head.Skip(4).Take(4).ToArray();

			if (!BitConverter.IsLittleEndian) {
				protoBytes.Reverse();
				lengthBytes.Reverse();
			}

			proto = BitConverter.ToInt32(protoBytes, 0);
			length = BitConverter.ToInt32(lengthBytes, 0);

			if (IsInvalidLength(length)) {
				throw new NetInvalidMessage();
			}
		}

		public static bool IsInvalidLength(int length) {
			return length <= 0 || length > MessageBufferSize;
		}
	}

	public class Task {
		public enum TaskType {
			Notify,
			Request
		}

		public readonly TaskType taskType;
		public readonly Message sendMessage;
		public readonly Net.ResponseDelegate callback;

		public Message recvMessage;
		public NetError error;

		public Task(Message message, TaskType taskType, Net.ResponseDelegate callback = null) {
			this.sendMessage = message;
			this.taskType = taskType;
			this.callback = callback;
		}

		public void OnCallback() {
			if (callback != null) {
				callback(recvMessage, error);
			}
		}
	}

	public class Net {
		private TcpClient tcpClinet;
		public readonly string hostname;
		public readonly int port;
		public readonly ResponseDelegate notifyCallback;
		private ConnectDelegate connectCallback { get; set; }
		public bool isWorking { get; private set; }
		public bool isConnecting { get { return asyncConnectResult != null; } }
		private IAsyncResult asyncConnectResult { get; set; }
		private Queue<Task> sendQueue { get; set; }
		private Queue<Task> recvQueue { get; set; }
		private Queue<Task> completeQueue { get; set; }
		private AutoResetEvent writeableEvent { get; set; }
		private Thread writeThread { get; set; }
		private Thread readThread { get; set; }

		public delegate void ConnectDelegate(NetError netError);
		public delegate void ResponseDelegate(Message message, NetError error);

		public Net(string hostname, int port, ResponseDelegate notifyCallback) {
			tcpClinet = new TcpClient();
			this.hostname = hostname;
			this.port = port;
			this.notifyCallback = notifyCallback;

			isWorking = false;

			sendQueue = new Queue<Task>();
			recvQueue = new Queue<Task>();
			completeQueue = new Queue<Task>();

			writeableEvent = new AutoResetEvent(false);
		}

		public void Connect(ConnectDelegate connectCallback) {
			if (isWorking) { throw new NetIsWorking(); }
			if (isConnecting) { throw new NetIsConnecting(); }

			try {
				asyncConnectResult = tcpClinet.BeginConnect(hostname, port, null, null);
			} catch (Exception exception) {
				asyncConnectResult = null;
				connectCallback(new NetError(exception));
			}

			this.connectCallback = connectCallback;
		}

		public void SendRequest(Message message, ResponseDelegate callback) {
			if (!isWorking) { throw new NetIsClosed(); }

			lock (sendQueue) {
				sendQueue.Enqueue(new Task(message, Task.TaskType.Request, callback));
				writeableEvent.Set();
			}
		}

		public void SendNotify(Message message) {
			if (!isWorking) { throw new NetIsClosed(); }

			lock (sendQueue) {
				sendQueue.Enqueue(new Task(message, Task.TaskType.Notify, notifyCallback));
				writeableEvent.Set();
			}
		}

		public void Update() {
			UpdateConnectingState();

			if (!isWorking) {
				return;
			}

			lock (completeQueue) {
				while (completeQueue.Count > 0) {
					completeQueue.Dequeue().OnCallback();

					if (!isWorking) {
						return;
					}
				}
			}
		}

		public void Close() {
			if (!isWorking) {
				isWorking = false;
				StopConnecting();
				if (tcpClinet.Connected) {
					CloseTcpClient();
				}
				return;
			}

			isWorking = false;
			CloseTcpClient();

			writeableEvent.Set();
			readThread.Join();
			writeThread.Join();

			Action<Queue<Task>> hander = (Queue<Task> queue) => {
				lock (queue) {
					while (queue.Count > 0) {
						var task = queue.Dequeue();
						if (task.taskType != Task.TaskType.Notify) {
							task.error = new NetError(new NetIsClosed());
							task.OnCallback();
						}
					}
				}
			};

			hander(completeQueue);
			hander(recvQueue);
			hander(sendQueue);

			if (notifyCallback != null) {
				notifyCallback(null, new NetError(new NetIsClosed()));
			}
		}

		private void WriteThread() {
			while (isWorking) {
				writeableEvent.WaitOne();

				Queue<Task> taskQueue = null;

				lock (sendQueue) {
					if (sendQueue.Count == 0) {
						continue;
					}

					taskQueue = sendQueue;
					sendQueue = new Queue<Task>();
				}

				foreach (var task in taskQueue) {
					var buff = task.sendMessage.GetData();
					lock (recvQueue) {
						try {
							var stream = tcpClinet.GetStream();
							stream.Write(buff, 0, buff.Length);
						} catch (Exception e) {
							if (e is IOException || e is ObjectDisposedException) {
								task.error = new NetError(e);
							} else {
								throw e;
							}
						} finally {
							if (task.taskType == Task.TaskType.Notify) {
								/// 如果 Notify 类型的任务发送失败
								/// 则伪造一个 Request 类型的错误
								/// 投递到 recvQueue
								/// notifyCallback 只接受连接关闭的错误
								/// 其他所有错误都投递到 request 的回调中供处理
								if (task.error != null) {
									var task_ = new Task(task.sendMessage, Task.TaskType.Request);
									task_.error = task.error;
									recvQueue.Enqueue(task_);
								} 
							} else {
								recvQueue.Enqueue(task);
							}
						}
					}
				}
			}
		}

		private Message ReadMessage(NetworkStream stream) {
			byte[] head = new byte[Message.HeadSize];
			var readedLength = stream.Read(head, 0, Message.HeadSize);
			if (readedLength == 0) {
				throw new NetRemoteClose();
			}

			int proto, length;
			Message.DecodeHead(head, out proto, out length);

			byte[] buffer = new byte[length];
			readedLength = stream.Read(buffer, 0, length);
			if (readedLength == 0) {
				throw new NetRemoteClose();
			}

			return new Message(proto, buffer);
		}

		private void ReadThread() {
			while (isWorking) {
				var taskQueue = new Queue<Task>();
				Exception exception = null;
				Message message = null;

				try {
					var stream = tcpClinet.GetStream();
					message = ReadMessage(stream);
				} catch (Exception e) {
					if (e is ArgumentNullException || e is ArgumentOutOfRangeException) {
						throw e;
					} else {
						exception = e;
					}
				} finally {
					Task task = null;

					lock (recvQueue) {
						while (recvQueue.Count > 0) {
							var task_ = recvQueue.Peek();
							if (task_.error != null) {
								taskQueue.Enqueue(recvQueue.Dequeue());
							} else {
								task = task_;
								break;
							}
						}

						/// 如果收到的是一个 notify 则新建一个 task 放入 completeQueue
						/// 否则将 recvQueue 中的 task 移动到 completeQueue
						if (task == null || (exception == null && task.sendMessage.proto != message.proto)) {
							task = new Task(null, Task.TaskType.Notify, notifyCallback);
						} else {
							recvQueue.Dequeue();
						}
					}

					task.recvMessage = message;
					task.error = new NetError(exception);
					taskQueue.Enqueue(task);

					lock (completeQueue) {
						while (taskQueue.Count > 0) {
							completeQueue.Enqueue(taskQueue.Dequeue());
						}
					}
				}
			}
		}

		private void CloseTcpClient() {
			tcpClinet.GetStream().Close();
			tcpClinet.Close();
			tcpClinet = new TcpClient();
		}

		private void StopConnecting() {
			if (asyncConnectResult != null) {
				asyncConnectResult.AsyncWaitHandle.WaitOne();
				try {
					tcpClinet.EndConnect(asyncConnectResult);
				} catch (Exception) {
				} finally {
					asyncConnectResult = null;
					var callback = connectCallback;
					connectCallback = null;
					/// callback 中可能直接 connect 并需要记录新的 connectCallback
					/// 所以无法直接 connectCallback() 然后 connnectCallback = null
					callback(new NetError(new NetIsClosed()));
				}
			}
		}

		private void UpdateConnectingState() {
			if (asyncConnectResult != null && asyncConnectResult.IsCompleted) {
				Exception exception = null;

				try {
					tcpClinet.EndConnect(asyncConnectResult);
				} catch (Exception e) {
					exception = e;
				} finally {
					if (exception == null) {
						isWorking = true;

						writeThread = new Thread(new ThreadStart(WriteThread));
						readThread = new Thread(new ThreadStart(ReadThread));
						//writeThread.IsBackground = false;
						//readThread.IsBackground = false;
						writeThread.Start();
						readThread.Start();
					}

					asyncConnectResult = null;
					var callback = connectCallback;
					connectCallback = null;
					/// callback 中可能直接 connect 并需要记录新的 connectCallback
					/// 所以无法直接 connectCallback() 然后 connnectCallback = null
					callback(new NetError(exception));
				}
			}
		}
	}
}