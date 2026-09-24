
namespace VRZ.Network
{

    public class LobbyMap : MapDefault
    {
        private void Start()
        {
            if (NetworkManager.instance != null)
                NetworkManager.instance.onPlayerSpawn += SpawnCharacter;
        }
        private void OnDisable()
        {
            // The manager destroys itself on shutdown, so it can be gone by the time this
            // scene unloads or Play stops. GameMap already guarded this; LobbyMap did not.
            if (NetworkManager.instance != null)
                NetworkManager.instance.onPlayerSpawn -= SpawnCharacter;
        }

    }
}
