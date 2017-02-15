#pragma once

#include <Windows.h>
#include "ChipsChallenge\MainWindow.h"

#define WINDOW_CLASS_MAX_LENGTH 100

namespace WindowsChipsChallenge
{
	class WindowsMainWindow : public ChipsChallenge::MainWindow
	{
	public:
		WindowsMainWindow(HINSTANCE, int);

		void Create() override;
		void Show() override;

	private:
		static LRESULT CALLBACK ProcessMessage(HWND, UINT, WPARAM, LPARAM);

		void RegisterWindowClass();

		HINSTANCE m_instance;
		int m_cmdShow;
		TCHAR m_windowClass[WINDOW_CLASS_MAX_LENGTH];
		HWND m_windowHandle;
	};
}