#include <Windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <exception>
#include <functional>
#include <future>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

#ifndef UNITY_INTERFACE_API
#define UNITY_INTERFACE_API __stdcall
#endif

namespace {
	const wchar_t* WindowClassName = L"Camera2WindowOutputWindow";
	const wchar_t* ControlWindowClassName = L"Camera2WindowOutputControlWindow";
	constexpr UINT WM_UI_TASK = WM_APP + 1;
	constexpr UINT WM_UI_STOP = WM_APP + 2;
	constexpr int FixedWidth = 1920;
	constexpr int FixedHeight = 1080;
	constexpr DWORD FixedWindowStyle = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;

	struct Output {
		int handle = 0;
		HWND hwnd = nullptr;
		bool closeRequested = false;
		bool visible = true;
		int width = FixedWidth;
		int height = FixedHeight;
		ComPtr<ID3D11Texture2D> source;
		ComPtr<ID3D11Device> device;
		ComPtr<ID3D11DeviceContext> context;
		ComPtr<IDXGISwapChain> swapChain;
		ComPtr<ID3D11RenderTargetView> renderTargetView;
		ComPtr<ID3D11ShaderResourceView> sourceView;
		ComPtr<ID3D11VertexShader> vertexShader;
		ComPtr<ID3D11PixelShader> pixelShader;
		ComPtr<ID3D11PixelShader> pixelShaderMSAA2;
		ComPtr<ID3D11PixelShader> pixelShaderMSAA4;
		ComPtr<ID3D11PixelShader> pixelShaderMSAA8;
		ComPtr<ID3D11SamplerState> sampler;
		ComPtr<ID3D11Buffer> pixelConstants;
		UINT sourceSampleCount = 1;
		UINT sourceWidth = 0;
		UINT sourceHeight = 0;
	};

	struct PixelConstants {
		float sourceSize[2] = {};
		UINT sampleCount = 1;
		UINT padding = 0;
	};

	std::mutex g_mutex;
	std::unordered_map<int, std::shared_ptr<Output>> g_outputs;
	int g_nextHandle = 1;
	HINSTANCE g_instance = nullptr;

	std::mutex g_uiMutex;
	std::condition_variable g_uiReady;
	std::thread* g_uiThread = nullptr;
	DWORD g_uiThreadId = 0;
	HWND g_controlWindow = nullptr;
	bool g_uiStartupComplete = false;

	struct UiTask {
		std::function<void()> action;
		std::promise<void> completed;
	};

	Output* FindOutputLocked(int handle) {
		auto it = g_outputs.find(handle);
		return it == g_outputs.end() ? nullptr : it->second.get();
	}

	std::shared_ptr<Output> FindOutputRefLocked(int handle) {
		auto it = g_outputs.find(handle);
		return it == g_outputs.end() ? nullptr : it->second;
	}

	void ResetD3DResources(Output& output, bool resetSwapChain) {
		output.renderTargetView.Reset();
		output.sourceView.Reset();
		output.vertexShader.Reset();
		output.pixelShader.Reset();
		output.pixelShaderMSAA2.Reset();
		output.pixelShaderMSAA4.Reset();
		output.pixelShaderMSAA8.Reset();
		output.sampler.Reset();
		output.pixelConstants.Reset();
		if(resetSwapChain)
			output.swapChain.Reset();
		output.context.Reset();
		output.device.Reset();
		output.sourceSampleCount = 1;
		output.sourceWidth = 0;
		output.sourceHeight = 0;
	}

	DXGI_FORMAT ToShaderResourceFormat(DXGI_FORMAT format) {
		switch(format) {
			case DXGI_FORMAT_R8G8B8A8_TYPELESS:
				return DXGI_FORMAT_R8G8B8A8_UNORM;
			case DXGI_FORMAT_B8G8R8A8_TYPELESS:
				return DXGI_FORMAT_B8G8R8A8_UNORM;
			case DXGI_FORMAT_B8G8R8X8_TYPELESS:
				return DXGI_FORMAT_B8G8R8X8_UNORM;
			case DXGI_FORMAT_R16G16B16A16_TYPELESS:
				return DXGI_FORMAT_R16G16B16A16_FLOAT;
			case DXGI_FORMAT_R10G10B10A2_TYPELESS:
				return DXGI_FORMAT_R10G10B10A2_UNORM;
			default:
				return format;
		}
	}

	bool CompileShader(const char* source, const char* entryPoint, const char* target, ComPtr<ID3DBlob>& shader) {
		UINT flags = D3DCOMPILE_ENABLE_STRICTNESS;
#if defined(_DEBUG)
		flags |= D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
#endif

		ComPtr<ID3DBlob> errors;
		return SUCCEEDED(D3DCompile(
			source,
			strlen(source),
			nullptr,
			nullptr,
			nullptr,
			entryPoint,
			target,
			flags,
			0,
			shader.GetAddressOf(),
			errors.GetAddressOf()
		));
	}

	const char* WindowOutputShaderSource = R"(
		struct VSOutput {
			float4 position : SV_Position;
			float2 uv : TEXCOORD0;
		};

		cbuffer PixelConstants : register(b0) {
			float2 sourceSize;
			uint sampleCount;
			uint padding;
		};

		Texture2D sourceTexture : register(t0);
		Texture2DMS<float4, 2> sourceTextureMSAA2 : register(t1);
		Texture2DMS<float4, 4> sourceTextureMSAA4 : register(t2);
		Texture2DMS<float4, 8> sourceTextureMSAA8 : register(t3);
		SamplerState sourceSampler : register(s0);

		VSOutput VS(uint id : SV_VertexID) {
			float2 uv = float2((id << 1) & 2, id & 2);
			VSOutput output;
			output.position = float4(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f, 0.0f, 1.0f);
			output.uv = float2(uv.x, 1.0f - uv.y);
			return output;
		}

		float4 PS(VSOutput input) : SV_Target {
			uint2 coord = min(uint2(input.uv * sourceSize), uint2(sourceSize) - uint2(1, 1));
			return sourceTexture.Load(int3(coord, 0));
		}

		float4 PSMSAA2(VSOutput input) : SV_Target {
			uint2 coord = min(uint2(input.uv * sourceSize), uint2(sourceSize) - uint2(1, 1));
			return (sourceTextureMSAA2.Load(coord, 0) + sourceTextureMSAA2.Load(coord, 1)) / 2.0f;
		}

		float4 PSMSAA4(VSOutput input) : SV_Target {
			uint2 coord = min(uint2(input.uv * sourceSize), uint2(sourceSize) - uint2(1, 1));
			float4 color =
				sourceTextureMSAA4.Load(coord, 0) +
				sourceTextureMSAA4.Load(coord, 1) +
				sourceTextureMSAA4.Load(coord, 2) +
				sourceTextureMSAA4.Load(coord, 3);
			return color / 4.0f;
		}

		float4 PSMSAA8(VSOutput input) : SV_Target {
			uint2 coord = min(uint2(input.uv * sourceSize), uint2(sourceSize) - uint2(1, 1));
			float4 color =
				sourceTextureMSAA8.Load(coord, 0) +
				sourceTextureMSAA8.Load(coord, 1) +
				sourceTextureMSAA8.Load(coord, 2) +
				sourceTextureMSAA8.Load(coord, 3) +
				sourceTextureMSAA8.Load(coord, 4) +
				sourceTextureMSAA8.Load(coord, 5) +
				sourceTextureMSAA8.Load(coord, 6) +
				sourceTextureMSAA8.Load(coord, 7);
			return color / 8.0f;
		}
	)";

	bool EnsureDeviceResources(Output& output) {
		if(!output.source)
			return false;

		if(!output.device) {
			output.source->GetDevice(output.device.GetAddressOf());
			if(output.device)
				output.device->GetImmediateContext(output.context.GetAddressOf());
		}

		if(!output.device || !output.context)
			return false;

		if(!output.vertexShader) {
			ComPtr<ID3DBlob> vertexShaderBlob;
			if(!CompileShader(WindowOutputShaderSource, "VS", "vs_4_0", vertexShaderBlob) ||
			   FAILED(output.device->CreateVertexShader(vertexShaderBlob->GetBufferPointer(), vertexShaderBlob->GetBufferSize(), nullptr, output.vertexShader.GetAddressOf())))
				return false;
		}

		if(!output.pixelShader) {
			ComPtr<ID3DBlob> pixelShaderBlob;
			if(!CompileShader(WindowOutputShaderSource, "PS", "ps_4_0", pixelShaderBlob) ||
			   FAILED(output.device->CreatePixelShader(pixelShaderBlob->GetBufferPointer(), pixelShaderBlob->GetBufferSize(), nullptr, output.pixelShader.GetAddressOf())))
				return false;
		}

		if(!output.pixelShaderMSAA2) {
			ComPtr<ID3DBlob> pixelShaderBlob;
			if(!CompileShader(WindowOutputShaderSource, "PSMSAA2", "ps_4_0", pixelShaderBlob) ||
			   FAILED(output.device->CreatePixelShader(pixelShaderBlob->GetBufferPointer(), pixelShaderBlob->GetBufferSize(), nullptr, output.pixelShaderMSAA2.GetAddressOf())))
				return false;
		}

		if(!output.pixelShaderMSAA4) {
			ComPtr<ID3DBlob> pixelShaderBlob;
			if(!CompileShader(WindowOutputShaderSource, "PSMSAA4", "ps_4_0", pixelShaderBlob) ||
			   FAILED(output.device->CreatePixelShader(pixelShaderBlob->GetBufferPointer(), pixelShaderBlob->GetBufferSize(), nullptr, output.pixelShaderMSAA4.GetAddressOf())))
				return false;
		}

		if(!output.pixelShaderMSAA8) {
			ComPtr<ID3DBlob> pixelShaderBlob;
			if(!CompileShader(WindowOutputShaderSource, "PSMSAA8", "ps_4_0", pixelShaderBlob) ||
			   FAILED(output.device->CreatePixelShader(pixelShaderBlob->GetBufferPointer(), pixelShaderBlob->GetBufferSize(), nullptr, output.pixelShaderMSAA8.GetAddressOf())))
				return false;
		}

		if(!output.sampler) {
			D3D11_SAMPLER_DESC desc = {};
			desc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
			desc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
			desc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
			desc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
			desc.ComparisonFunc = D3D11_COMPARISON_NEVER;
			desc.MinLOD = 0.0f;
			desc.MaxLOD = D3D11_FLOAT32_MAX;
			if(FAILED(output.device->CreateSamplerState(&desc, output.sampler.GetAddressOf())))
				return false;
		}

		if(!output.pixelConstants) {
			D3D11_BUFFER_DESC desc = {};
			desc.ByteWidth = sizeof(PixelConstants);
			desc.Usage = D3D11_USAGE_DEFAULT;
			desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
			if(FAILED(output.device->CreateBuffer(&desc, nullptr, output.pixelConstants.GetAddressOf())))
				return false;
		}

		if(!output.sourceView) {
			D3D11_TEXTURE2D_DESC sourceDesc = {};
			output.source->GetDesc(&sourceDesc);
			output.sourceWidth = sourceDesc.Width;
			output.sourceHeight = sourceDesc.Height;
			output.sourceSampleCount = sourceDesc.SampleDesc.Count == 0 ? 1 : sourceDesc.SampleDesc.Count;

			D3D11_SHADER_RESOURCE_VIEW_DESC viewDesc = {};
			viewDesc.Format = ToShaderResourceFormat(sourceDesc.Format);
			if(output.sourceSampleCount > 1) {
				viewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DMS;
			} else {
				viewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
				viewDesc.Texture2D.MipLevels = 1;
			}

			if(FAILED(output.device->CreateShaderResourceView(output.source.Get(), &viewDesc, output.sourceView.GetAddressOf())))
				return false;
		}

		return true;
	}

	LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
		if(message == WM_NCCREATE) {
			auto createStruct = reinterpret_cast<CREATESTRUCTW*>(lParam);
			SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(createStruct->lpCreateParams));
		}

		if(message == WM_CLOSE) {
			auto* output = reinterpret_cast<Output*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
			if(output) {
				{
					std::lock_guard<std::mutex> lock(g_mutex);
					output->closeRequested = true;
					output->visible = false;
				}
				ShowWindow(hwnd, SW_HIDE);
				return 0;
			}
		}

		if(message == WM_NCDESTROY)
			SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);

		if(message == WM_DESTROY)
			return 0;

		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	LRESULT CALLBACK ControlWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
		(void)hwnd;
		(void)wParam;

		if(message == WM_UI_TASK) {
			std::unique_ptr<UiTask> task(reinterpret_cast<UiTask*>(lParam));
			try {
				task->action();
				task->completed.set_value();
			} catch(...) {
				task->completed.set_exception(std::current_exception());
			}
			return 0;
		}

		if(message == WM_UI_STOP) {
			DestroyWindow(g_controlWindow);
			PostQuitMessage(0);
			return 0;
		}

		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	void RegisterWindowClasses() {
		static bool registered = false;
		if(registered)
			return;

		WNDCLASSEXW outputWindowClass = {};
		outputWindowClass.cbSize = sizeof(outputWindowClass);
		outputWindowClass.lpfnWndProc = WindowProc;
		outputWindowClass.hInstance = g_instance;
		outputWindowClass.hCursor = LoadCursor(nullptr, IDC_ARROW);
		outputWindowClass.lpszClassName = WindowClassName;

		WNDCLASSEXW controlWindowClass = {};
		controlWindowClass.cbSize = sizeof(controlWindowClass);
		controlWindowClass.lpfnWndProc = ControlWindowProc;
		controlWindowClass.hInstance = g_instance;
		controlWindowClass.lpszClassName = ControlWindowClassName;

		RegisterClassExW(&outputWindowClass);
		RegisterClassExW(&controlWindowClass);
		registered = true;
	}

	bool CreateSwapChain(Output& output) {
		if(output.swapChain || !output.device || !output.hwnd)
			return output.swapChain != nullptr;

		ComPtr<IDXGIDevice> dxgiDevice;
		ComPtr<IDXGIAdapter> adapter;
		ComPtr<IDXGIFactory> factory;

		if(FAILED(output.device.As(&dxgiDevice)) ||
		   FAILED(dxgiDevice->GetAdapter(&adapter)) ||
		   FAILED(adapter->GetParent(__uuidof(IDXGIFactory), reinterpret_cast<void**>(factory.GetAddressOf()))))
			return false;

		DXGI_SWAP_CHAIN_DESC desc = {};
		desc.BufferDesc.Width = output.width;
		desc.BufferDesc.Height = output.height;
		desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
		desc.SampleDesc.Count = 1;
		desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
		desc.BufferCount = 2;
		desc.OutputWindow = output.hwnd;
		desc.Windowed = TRUE;
		desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

		return SUCCEEDED(factory->CreateSwapChain(output.device.Get(), &desc, output.swapChain.GetAddressOf()));
	}

	void UiThreadMain() {
		RegisterWindowClasses();

		HWND controlWindow = CreateWindowExW(
			0,
			ControlWindowClassName,
			L"Camera2WindowOutputControl",
			0,
			0,
			0,
			0,
			0,
			HWND_MESSAGE,
			nullptr,
			g_instance,
			nullptr
		);

		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			g_uiThreadId = GetCurrentThreadId();
			g_controlWindow = controlWindow;
			g_uiStartupComplete = true;
		}
		g_uiReady.notify_all();

		if(!controlWindow)
			return;

		MSG msg = {};
		while(GetMessageW(&msg, nullptr, 0, 0) > 0) {
			TranslateMessage(&msg);
			DispatchMessageW(&msg);
		}

		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			if(g_controlWindow == controlWindow)
				g_controlWindow = nullptr;
			g_uiThreadId = 0;
			g_uiStartupComplete = false;
		}
	}

	void StopUiThread() {
		std::thread* uiThread = nullptr;
		HWND controlWindow = nullptr;

		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			uiThread = g_uiThread;
			controlWindow = g_controlWindow;
		}

		if(!uiThread)
			return;

		if(controlWindow)
			PostMessageW(controlWindow, WM_UI_STOP, 0, 0);

		if(uiThread->joinable() && uiThread->get_id() != std::this_thread::get_id())
			uiThread->join();
		else if(uiThread->joinable())
			uiThread->detach();

		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			if(g_uiThread == uiThread)
				g_uiThread = nullptr;
			g_controlWindow = nullptr;
			g_uiThreadId = 0;
			g_uiStartupComplete = false;
		}

		delete uiThread;
	}

	bool EnsureUiThread() {
		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			if(g_controlWindow)
				return true;
		}

		std::unique_lock<std::mutex> lock(g_uiMutex);
		if(!g_uiThread) {
			g_uiStartupComplete = false;
			try {
				g_uiThread = new std::thread(UiThreadMain);
			} catch(...) {
				g_uiThread = nullptr;
				g_uiStartupComplete = true;
				return false;
			}
		}

		g_uiReady.wait(lock, [] { return g_uiStartupComplete; });
		const bool started = g_controlWindow != nullptr;
		lock.unlock();

		if(!started)
			StopUiThread();

		return started;
	}

	bool PostUiTask(std::function<void()> action, bool wait) {
		if(!EnsureUiThread())
			return false;

		DWORD uiThreadId = 0;
		HWND controlWindow = nullptr;
		{
			std::lock_guard<std::mutex> lock(g_uiMutex);
			uiThreadId = g_uiThreadId;
			controlWindow = g_controlWindow;
		}

		if(!controlWindow)
			return false;

		if(GetCurrentThreadId() == uiThreadId) {
			action();
			return true;
		}

		auto task = std::make_unique<UiTask>();
		task->action = std::move(action);
		auto completed = task->completed.get_future();

		if(!PostMessageW(controlWindow, WM_UI_TASK, 0, reinterpret_cast<LPARAM>(task.get())))
			return false;

		task.release();

		if(wait)
			completed.get();

		return true;
	}

	void StopUiThreadIfIdle() {
		bool idle = false;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			idle = g_outputs.empty();
		}

		if(idle)
			StopUiThread();
	}

	bool EnsureRenderTargetView(Output& output) {
		if(output.renderTargetView)
			return true;

		if(!CreateSwapChain(output))
			return false;

		ComPtr<ID3D11Texture2D> backBuffer;
		if(FAILED(output.swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(backBuffer.GetAddressOf()))))
			return false;

		return SUCCEEDED(output.device->CreateRenderTargetView(backBuffer.Get(), nullptr, output.renderTargetView.GetAddressOf()));
	}

	struct SavedPipelineState {
		ID3D11InputLayout* inputLayout = nullptr;
		D3D11_PRIMITIVE_TOPOLOGY primitiveTopology = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
		ID3D11Buffer* vertexBuffer = nullptr;
		UINT vertexStride = 0;
		UINT vertexOffset = 0;
		ID3D11VertexShader* vertexShader = nullptr;
		ID3D11PixelShader* pixelShader = nullptr;
		ID3D11ShaderResourceView* pixelShaderResources[4] = {};
		ID3D11SamplerState* pixelSampler = nullptr;
		ID3D11Buffer* pixelConstantBuffer = nullptr;
		ID3D11RasterizerState* rasterizerState = nullptr;
		UINT viewportCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
		D3D11_VIEWPORT viewports[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] = {};
		UINT scissorCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
		D3D11_RECT scissors[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] = {};
		ID3D11RenderTargetView* renderTargetView = nullptr;
		ID3D11DepthStencilView* depthStencilView = nullptr;
		ID3D11BlendState* blendState = nullptr;
		FLOAT blendFactor[4] = {};
		UINT sampleMask = 0;
		ID3D11DepthStencilState* depthStencilState = nullptr;
		UINT stencilRef = 0;

		void Capture(ID3D11DeviceContext* context) {
			context->IAGetInputLayout(&inputLayout);
			context->IAGetPrimitiveTopology(&primitiveTopology);
			context->IAGetVertexBuffers(0, 1, &vertexBuffer, &vertexStride, &vertexOffset);
			context->VSGetShader(&vertexShader, nullptr, nullptr);
			context->PSGetShader(&pixelShader, nullptr, nullptr);
			context->PSGetShaderResources(0, 4, pixelShaderResources);
			context->PSGetSamplers(0, 1, &pixelSampler);
			context->PSGetConstantBuffers(0, 1, &pixelConstantBuffer);
			context->RSGetState(&rasterizerState);
			context->RSGetViewports(&viewportCount, viewports);
			context->RSGetScissorRects(&scissorCount, scissors);
			context->OMGetRenderTargets(1, &renderTargetView, &depthStencilView);
			context->OMGetBlendState(&blendState, blendFactor, &sampleMask);
			context->OMGetDepthStencilState(&depthStencilState, &stencilRef);
		}

		void Restore(ID3D11DeviceContext* context) {
			ID3D11Buffer* vertexBuffers[1] = { vertexBuffer };
			ID3D11SamplerState* pixelSamplers[1] = { pixelSampler };
			ID3D11Buffer* pixelConstantBuffers[1] = { pixelConstantBuffer };
			ID3D11RenderTargetView* renderTargetViews[1] = { renderTargetView };

			context->IASetInputLayout(inputLayout);
			context->IASetPrimitiveTopology(primitiveTopology);
			context->IASetVertexBuffers(0, 1, vertexBuffers, &vertexStride, &vertexOffset);
			context->VSSetShader(vertexShader, nullptr, 0);
			context->PSSetShader(pixelShader, nullptr, 0);
			context->PSSetShaderResources(0, 4, pixelShaderResources);
			context->PSSetSamplers(0, 1, pixelSamplers);
			context->PSSetConstantBuffers(0, 1, pixelConstantBuffers);
			context->RSSetState(rasterizerState);
			context->RSSetViewports(viewportCount, viewports);
			context->RSSetScissorRects(scissorCount, scissors);
			context->OMSetRenderTargets(1, renderTargetViews, depthStencilView);
			context->OMSetBlendState(blendState, blendFactor, sampleMask);
			context->OMSetDepthStencilState(depthStencilState, stencilRef);

			if(inputLayout) inputLayout->Release();
			if(vertexBuffer) vertexBuffer->Release();
			if(vertexShader) vertexShader->Release();
			if(pixelShader) pixelShader->Release();
			if(pixelShaderResources[0]) pixelShaderResources[0]->Release();
			if(pixelShaderResources[1]) pixelShaderResources[1]->Release();
			if(pixelShaderResources[2]) pixelShaderResources[2]->Release();
			if(pixelShaderResources[3]) pixelShaderResources[3]->Release();
			if(pixelSampler) pixelSampler->Release();
			if(pixelConstantBuffer) pixelConstantBuffer->Release();
			if(rasterizerState) rasterizerState->Release();
			if(renderTargetView) renderTargetView->Release();
			if(depthStencilView) depthStencilView->Release();
			if(blendState) blendState->Release();
			if(depthStencilState) depthStencilState->Release();
		}
	};

	bool RenderOutput(Output& output) {
		if(!EnsureDeviceResources(output) || !EnsureRenderTargetView(output))
			return false;

		PixelConstants constants = {};
		constants.sourceSize[0] = static_cast<float>(output.sourceWidth);
		constants.sourceSize[1] = static_cast<float>(output.sourceHeight);
		constants.sampleCount = output.sourceSampleCount;
		output.context->UpdateSubresource(output.pixelConstants.Get(), 0, nullptr, &constants, 0, 0);

		SavedPipelineState savedState;
		savedState.Capture(output.context.Get());

		UINT stride = 0;
		UINT offset = 0;
		ID3D11Buffer* nullBuffer = nullptr;
		D3D11_VIEWPORT viewport = {};
		viewport.Width = static_cast<float>(output.width);
		viewport.Height = static_cast<float>(output.height);
		viewport.MinDepth = 0.0f;
		viewport.MaxDepth = 1.0f;

		float blendFactor[4] = {};
		ID3D11PixelShader* selectedPixelShader = output.pixelShader.Get();
		if(output.sourceSampleCount == 2)
			selectedPixelShader = output.pixelShaderMSAA2.Get();
		else if(output.sourceSampleCount == 4)
			selectedPixelShader = output.pixelShaderMSAA4.Get();
		else if(output.sourceSampleCount >= 8)
			selectedPixelShader = output.pixelShaderMSAA8.Get();

		output.context->IASetInputLayout(nullptr);
		output.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		output.context->IASetVertexBuffers(0, 1, &nullBuffer, &stride, &offset);
		output.context->VSSetShader(output.vertexShader.Get(), nullptr, 0);
		output.context->PSSetShader(selectedPixelShader, nullptr, 0);
		ID3D11SamplerState* samplers[1] = { output.sampler.Get() };
		ID3D11Buffer* constantBuffers[1] = { output.pixelConstants.Get() };
		ID3D11RenderTargetView* renderTargetViews[1] = { output.renderTargetView.Get() };
		ID3D11ShaderResourceView* sourceViews[4] = {};
		if(output.sourceSampleCount == 2)
			sourceViews[1] = output.sourceView.Get();
		else if(output.sourceSampleCount == 4)
			sourceViews[2] = output.sourceView.Get();
		else if(output.sourceSampleCount >= 8)
			sourceViews[3] = output.sourceView.Get();
		else
			sourceViews[0] = output.sourceView.Get();
		output.context->PSSetSamplers(0, 1, samplers);
		output.context->PSSetConstantBuffers(0, 1, constantBuffers);
		output.context->RSSetState(nullptr);
		output.context->RSSetViewports(1, &viewport);
		output.context->OMSetRenderTargets(1, renderTargetViews, nullptr);
		output.context->OMSetBlendState(nullptr, blendFactor, 0xffffffff);
		output.context->OMSetDepthStencilState(nullptr, 0);
		output.context->PSSetShaderResources(0, 4, sourceViews);
		output.context->Draw(3, 0);

		ID3D11ShaderResourceView* nullViews[4] = {};
		output.context->PSSetShaderResources(0, 4, nullViews);
		savedState.Restore(output.context.Get());

		output.swapChain->Present(0, 0);
		return true;
	}

	void UNITY_INTERFACE_API OnRenderEvent(int eventId) {
		std::lock_guard<std::mutex> lock(g_mutex);
		auto* output = FindOutputLocked(eventId);
		if(!output)
			return;

		if(output->closeRequested || !output->visible || !output->source || !output->hwnd)
			return;

		RenderOutput(*output);
	}
}

extern "C" {
	typedef void(__stdcall* UnityRenderingEvent)(int eventId);
	__declspec(dllexport) void __cdecl DestroyWindowOutput(int handle);

	__declspec(dllexport) int __cdecl CreateWindowOutput(const wchar_t* title, int width, int height) {
		if(!EnsureUiThread())
			return 0;

		auto output = std::make_shared<Output>();
		output->width = width > 0 ? width : FixedWidth;
		output->height = height > 0 ? height : FixedHeight;

		const std::wstring windowTitle = title == nullptr ? L"Camera2" : title;
		int handle = 0;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			handle = g_nextHandle++;
			output->handle = handle;
			g_outputs.emplace(handle, output);
		}

		const bool created = PostUiTask([output, windowTitle] {
			int outputWidth = FixedWidth;
			int outputHeight = FixedHeight;
			bool visible = false;

			{
				std::lock_guard<std::mutex> lock(g_mutex);
				outputWidth = output->width;
				outputHeight = output->height;
				visible = output->visible && !output->closeRequested;
			}

			RECT rect = { 0, 0, outputWidth, outputHeight };
			AdjustWindowRect(&rect, FixedWindowStyle, FALSE);

			HWND hwnd = CreateWindowExW(
				0,
				WindowClassName,
				windowTitle.c_str(),
				FixedWindowStyle,
				CW_USEDEFAULT,
				CW_USEDEFAULT,
				rect.right - rect.left,
				rect.bottom - rect.top,
				nullptr,
				nullptr,
				g_instance,
				output.get()
			);

			if(!hwnd)
				return;

			{
				std::lock_guard<std::mutex> lock(g_mutex);
				output->hwnd = hwnd;
				visible = output->visible && !output->closeRequested;
			}

			ShowWindow(hwnd, visible ? SW_SHOW : SW_HIDE);
		}, true);

		bool hasWindow = false;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			hasWindow = output->hwnd != nullptr;
		}

		if(!created || !hasWindow) {
			DestroyWindowOutput(handle);
			return 0;
		}

		return handle;
	}

	__declspec(dllexport) void __cdecl DestroyWindowOutput(int handle) {
		std::shared_ptr<Output> output;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			auto it = g_outputs.find(handle);
			if(it == g_outputs.end())
				return;
			output = it->second;
			output->visible = false;
			output->closeRequested = true;
			output->source.Reset();
			ResetD3DResources(*output, true);
			g_outputs.erase(it);
		}

		PostUiTask([output] {
			HWND hwnd = nullptr;
			{
				std::lock_guard<std::mutex> lock(g_mutex);
				hwnd = output->hwnd;
				output->hwnd = nullptr;
			}

			if(hwnd)
				DestroyWindow(hwnd);
		}, true);

		StopUiThreadIfIdle();
	}

	__declspec(dllexport) void __cdecl SetSourceTexture(int handle, void* texture, int width, int height) {
		(void)width;
		(void)height;

		std::lock_guard<std::mutex> lock(g_mutex);
		auto* output = FindOutputLocked(handle);
		if(!output)
			return;

		output->source.Reset();
		ResetD3DResources(*output, true);

		if(texture)
			reinterpret_cast<ID3D11Texture2D*>(texture)->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(output->source.GetAddressOf()));
	}

	__declspec(dllexport) void __cdecl SetVisible(int handle, bool visible) {
		std::shared_ptr<Output> output;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			output = FindOutputRefLocked(handle);
			if(!output)
				return;

			output->visible = visible;
		}

		PostUiTask([output] {
			HWND hwnd = nullptr;
			bool shouldShow = false;
			{
				std::lock_guard<std::mutex> lock(g_mutex);
				hwnd = output->hwnd;
				shouldShow = output->visible && !output->closeRequested;
			}

			if(hwnd)
				ShowWindow(hwnd, shouldShow ? SW_SHOW : SW_HIDE);
		}, false);
	}

	__declspec(dllexport) void __cdecl SetWindowTitle(int handle, const wchar_t* title) {
		std::shared_ptr<Output> output;
		{
			std::lock_guard<std::mutex> lock(g_mutex);
			output = FindOutputRefLocked(handle);
			if(!output)
				return;
		}

		const std::wstring windowTitle = title == nullptr ? L"Camera2" : title;
		PostUiTask([output, windowTitle] {
			HWND hwnd = nullptr;
			{
				std::lock_guard<std::mutex> lock(g_mutex);
				hwnd = output->hwnd;
			}

			if(hwnd)
				SetWindowTextW(hwnd, windowTitle.c_str());
		}, false);
	}

	__declspec(dllexport) bool __cdecl IsCloseRequested(int handle) {
		std::lock_guard<std::mutex> lock(g_mutex);
		auto* output = FindOutputLocked(handle);
		return output == nullptr || output->closeRequested;
	}

	__declspec(dllexport) UnityRenderingEvent __cdecl GetRenderEventFunc() {
		return OnRenderEvent;
	}
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
	if(reason == DLL_PROCESS_ATTACH) {
		g_instance = module;
		DisableThreadLibraryCalls(module);
	} else if(reason == DLL_PROCESS_DETACH) {
		StopUiThread();
	}
	return TRUE;
}
