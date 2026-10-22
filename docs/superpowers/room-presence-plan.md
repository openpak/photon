# Friend-feed room presence bridge — implementation plan

1. Add a bounded in-memory mapping from derived identity to nx2 credential.
2. Extract `openpak` from Photon client-auth parameters at the Name Server.
3. Bind the derived identity through the existing signed Photon handoff token.
4. On Game Server room creation, publish `{room, app_id}` asynchronously to
   the account service.
5. Add focused extraction/registration tests and run the full server suite.
