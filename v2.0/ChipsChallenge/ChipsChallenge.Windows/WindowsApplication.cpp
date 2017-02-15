#include "WindowsApplication.h"
#include "resource.h"
#include "WindowsKeyboard.h"
#include "WindowsMainWindow.h"

namespace WindowsChipsChallenge
{
	WindowsApplication::WindowsApplication(HINSTANCE instance, int cmdShow)
		: m_instance(m_instance), m_cmdShow(cmdShow)
	{
		m_accelTable = LoadAccelerators(m_instance, MAKEINTRESOURCE(IDC_CHIPSCHALLENGE));
		Initialize();
	}

	int WindowsApplication::GetReturnCode() const
	{
		MSG msg;
		while (GetMessage(&msg, NULL, 0, 0))
		{
			if (!TranslateAccelerator(msg.hwnd, m_accelTable, &msg))
			{
				TranslateMessage(&msg);
				DispatchMessage(&msg);
			}
		}
		return (int) msg.wParam;
	}

	std::unique_ptr<ChipsChallenge::MainWindow> WindowsApplication::CreateMainWindow()
	{
		return std::unique_ptr<ChipsChallenge::MainWindow>(new WindowsMainWindow(m_instance, m_cmdShow));
	}

	ChipsChallenge::Keyboard WindowsApplication::CreateKeyboard()
	{
		return WindowsKeyboard();
	}
}
