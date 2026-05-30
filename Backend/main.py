from llm_agent import create_unity_agent, extract_json_from_response

# ==========================================
# 10. MODE INTERACTIF
# ==========================================
if __name__ == "__main__":
    print("\n" + "="*70)
    print("🏠 ARCHI-AGENT VR - ASSISTANT ARCHITECTURAL")
    print("="*70)
    print("💡 Exemples de commandes:")
    print("   • 'Crée une maison de 90m²'")
    print("   • 'Ajoute un salon'")
    print("   • 'Ajoute une chambre'")
    print("   • 'Ajoute une cuisine'")
    print("   • 'Ajoute une salle de bain'")
    print("   • 'quit' pour quitter")
    print("="*70 + "\n")
    
    agent = create_unity_agent()
    
    while True:
        try:
            msg = input("👤 Toi: ").strip()
            
            if msg.lower() in ["quit", "exit", "q"]:
                print("\n👋 À bientôt! Merci d'avoir utilisé Archi-Agent VR ✨\n")
                break
            
            if not msg:
                continue
            
            print("\n🤖 Agent (traitement...)\n")
            res = agent.run(msg)
            print(res.content)
            
            # Extraire et vérifier JSON
            json_data = extract_json_from_response(res.content)
            if json_data and json_data.get("rooms"):
                num_rooms = len(json_data['rooms'])
                score = json_data.get('metadata', {}).get('layout_score', 'N/A')
                surface_used = json_data.get('metadata', {}).get('surface_utilisee', 'N/A')
                print(f"\n✨ Mise à jour: {num_rooms} pièce(s) | Score: {score}/100 | Surface: {surface_used}m²\n")
            
        except KeyboardInterrupt:
            print("\n\n⚠️  Interruption. Tapez 'quit' pour quitter proprement.")
        except Exception as e:
            print(f"\n❌ Erreur: {e}\n")
