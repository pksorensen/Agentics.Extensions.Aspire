#!/bin/bash
echo "Building example terminal app..."
cat > "$(dirname "$0")/app.sh" << 'SCRIPT'
#!/bin/bash
echo "Welcome to the Aspire Terminal!"
echo "Type anything and press Enter. Type 'exit' to quit."
while true; do
    read -p "> " input
    if [ "$input" = "exit" ]; then
        echo "Goodbye!"
        exit 0
    fi
    echo "You said: $input"
done
SCRIPT
chmod +x "$(dirname "$0")/app.sh"
echo "Build complete."
