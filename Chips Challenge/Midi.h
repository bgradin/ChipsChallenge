#include "stdafx.h"

class MIDI
{
	boost::thread* th;
	int level;
	bool play;
	bool stopped;

	void loop()
	{
		mciSendString(L"open CHIP01.MID type sequencer alias Music1", NULL, 0, NULL);
		mciSendString(L"open CHIP02.MID type sequencer alias Music0", NULL, 0, NULL);

		while (true)
		{
			if (level == -1 || !stopped)
			{
				mciSendString(L"stop Music1", NULL, 0, NULL);
				mciSendString(L"stop Music0", NULL, 0, NULL);
				stopped = true;
				Sleep(100);
				continue;
			}

			if (!play && level > 0 && playMusic)
			{
				mciSendString(stows("seek Music" + itos(level % 2) + " to start").c_str(), NULL, 0, NULL);
				mciSendString(stows("play Music" + itos(level % 2)).c_str(), NULL, 0, NULL);
				play = true;
				Sleep(100);
				continue;
			}

			if (level != 0)
			{
				int playing = level % 2;

				TCHAR tch[100];

				mciSendString(stows("status Music" + itos(level % 2) + " mode").c_str(), tch, 100, NULL);
				wstring w = tch;
				string s(w.begin(), w.end());

				if (s == "stopped" && playMusic) 
				{
					mciSendString(stows("seek Music" + itos(level % 2) + " to start").c_str(), NULL, 0, NULL);
					mciSendString(stows("play Music" + itos(level % 2)).c_str(), NULL, 0, NULL);
				}
			}
			Sleep(100);
		}
	}

public:
	MIDI()
	{
		level = 0;
		play = false;
		stopped = true;
		th = new boost::thread(&MIDI::loop, &(*this));
	}
	~MIDI()
	{
		if(th) {
			th->interrupt();
			delete th;
			th = NULL;
		}
	}
	void start(const int num)
	{
		level = num;
	}
	void stop()
	{
		level = -1;
		play = false;
		stopped = false;
	}
};