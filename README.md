# Pharma 4.0 - ASP.NET Core Launch Pad

This project has been converted from the PHP/XAMPP prototype into an ASP.NET Core Razor Pages application.

## Entry Points

- `/Auth` - sliding sign-in/sign-up page
- `/Dashboard` - authenticated Pharma 4.0 Launch Pad
- `/Logout` - clears the active session
- `/Api/Tcodes?handler=List` - transaction directory JSON endpoint
- `/Api/Tcodes?handler=Launch&tcode=DASH` - transaction launch JSON endpoint

## Run Locally

```powershell
dotnet run --project "C:\Users\Carlo Mauring Lidres\OneDrive\Desktop\Projects\C# Project\Pharma4.0\Pharma40.csproj" --urls http://localhost:5088
```

Then open:

```text
http://localhost:5088/
```

## Notes

- Passwords are stored with PBKDF2 salted hashing.
- Sessions protect the Launch Pad.
- Local app data is created automatically in `App_Data/app-data.json`.
- The first registered user becomes `Administrator`; later users become `User`.
- Theme choices are available in the profile menu and saved in browser `localStorage`.
