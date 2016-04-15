using UnityEngine;
using System.Collections;
using Game;

public class Test : MonoBehaviour {
	private Net net { get; set; }

	void Start() {
		net = new Net("127.0.0.1", 2430, NotifyCallback);
	}

	void Send() {
		if (net.isWorking) {
			var content = System.Text.Encoding.UTF8.GetBytes("this is from client request.");
			net.SendRequest(new Message(10, content), (Message message, NetError error) => {
				if (error.exception == null) {
					var str = System.Text.Encoding.UTF8.GetString(message.message);
					Debug.LogFormat("requestCB: {0}", str);
				} else {
					Debug.Log(error.exception);
				}
			});

			content = System.Text.Encoding.UTF8.GetBytes("this is from client notify.");
			net.SendNotify(new Message(505, content));
		}
	}

	void NotifyCallback(Message message, NetError error) {
		if (error.exception == null) {
			var str = System.Text.Encoding.UTF8.GetString(message.message);
			Debug.LogFormat("notifyCB: {0}", str);
		} else {
			Debug.Log(error.exception);
		}
	}
		
	// Update is called once per frame
	void Update () {
		net.Update();
		Send();
	}

	public void Close() {
		net.Close();
	}

	public void Connect() {
		Debug.Log("start connect");
		net.Connect((NetError error) => {
			Debug.Log("connected");
			Debug.Log(error.exception);

			if (error.exception == null) {
				Send();
			}
		});
	}

	void OnDestroy() {
		net.Close();
	}
}
