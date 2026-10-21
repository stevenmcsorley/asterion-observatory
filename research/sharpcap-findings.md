**SharpCap reverse-engineering summary (REA 6.7.0)**

Target: installed SharpCap 4.2.15290 Beta, x64. REA inspected managed interfaces and selected camera provider assemblies. Findings and limitations are documented in the research folder linked from the project history; proprietary binaries and raw REA dumps are intentionally not published here.

Observed managed contracts include camera discovery, control values, capture state, and frame delivery. The ASI provider calls ZWO's `ASICamera2.dll` through C `cdecl` imports. The app uses ZWO's public SDK directly rather than SharpCap assemblies or implementation code.

Boundaries: the main SharpCap payload (`SharpCap.app.dat`) was not decoded by REA, and static inspection does not establish runtime UI behavior. Treat this as a feature/API map, not a complete reconstruction. Asterion is an independent implementation; it does not copy SharpCap code or assets.
