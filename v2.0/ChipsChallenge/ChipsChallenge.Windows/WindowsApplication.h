#pragma once

#include <memory>
#include <Windows.h>
#include "ChipsChallenge\Application.h"

namespace WindowsChipsChallenge
{
	class WindowsApplication : public ChipsChallenge::Application
	{
	public:
		WindowsApplication(HINSTANCE, int);

		int GetReturnCode() const;

	protected:
		std::unique_ptr<ChipsChallenge::MainWindow> CreateMainWindow() override;
		ChipsChallenge::Keyboard CreateKeyboard() override;

	private:
		int m_cmdShow;
		HINSTANCE m_instance;
		HACCEL m_accelTable;
	};
}