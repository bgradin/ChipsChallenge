#include <Windows.h>
#include "WindowsApplication.h"

#define WIN32_LEAN_AND_MEAN

int APIENTRY wWinMain(HINSTANCE instance, HINSTANCE prevInstance, LPTSTR cmdLine, int cmdShow)
{
	UNREFERENCED_PARAMETER(prevInstance);
	UNREFERENCED_PARAMETER(cmdLine);

	WindowsChipsChallenge::WindowsApplication application = WindowsChipsChallenge::WindowsApplication(instance, cmdShow);
	return application.GetReturnCode();
}