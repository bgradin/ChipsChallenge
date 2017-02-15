#include "WindowsMainWindow.h"
#include "resource.h"

namespace WindowsChipsChallenge
{
	WindowsMainWindow::WindowsMainWindow(HINSTANCE instance, int cmdShow)
		: m_instance(instance), m_cmdShow(cmdShow)
	{
	}

	void WindowsMainWindow::Create()
	{
		LoadString(m_instance, IDC_CHIPSCHALLENGE, m_windowClass, WINDOW_CLASS_MAX_LENGTH);
		RegisterWindowClass();
		m_windowHandle = CreateWindow(
			m_windowClass,
			TEXT(""),
			SS_BITMAP | WS_TILED | WS_SYSMENU | WS_MINIMIZEBOX,
			CW_USEDEFAULT,
			CW_USEDEFAULT,
			518,
			401,
			NULL,
			NULL,
			m_instance,
			NULL);
	}

	void WindowsMainWindow::Show()
	{
		if (!!m_windowHandle)
		{
			ShowWindow(m_windowHandle, m_cmdShow);
			UpdateWindow(m_windowHandle);
		}
		
	}

	void WindowsMainWindow::RegisterWindowClass()
	{
		WNDCLASSEX windowClass;
		windowClass.cbSize        = sizeof(WNDCLASSEX);
		windowClass.style         = CS_HREDRAW | CS_VREDRAW;
		windowClass.lpfnWndProc   = ProcessMessage;
		windowClass.cbClsExtra    = 0;
		windowClass.cbWndExtra    = 0;
		windowClass.hInstance     = m_instance;
		windowClass.hIcon         = LoadIcon(m_instance, MAKEINTRESOURCE(IDI_CHIPSCHALLENGE));
		windowClass.hIconSm       = LoadIcon(m_instance, MAKEINTRESOURCE(IDI_CHIPSCHALLENGE));
		windowClass.hCursor       = LoadCursor(NULL, IDC_ARROW);
		windowClass.hbrBackground = (HBRUSH) (COLOR_WINDOW + 1);
		windowClass.lpszMenuName  = MAKEINTRESOURCE(IDC_CHIPSCHALLENGE);
		windowClass.lpszClassName = m_windowClass;
		RegisterClassEx(&windowClass);
	}

	LRESULT CALLBACK WindowsMainWindow::ProcessMessage(HWND windowHandle, UINT message, WPARAM wParam, LPARAM lParam)
	{
		return DefWindowProc(windowHandle, message, wParam, lParam);
	}
}