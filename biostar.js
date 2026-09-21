import mysql from "mysql";
import fetch from "node-fetch";
import fs from "fs";
import dotenv from "dotenv";
import moment from "moment";
import https from "https";

dotenv.config();

// Create HTTPS agent to handle self-signed certificates
const httpsAgent = new https.Agent({
  rejectUnauthorized: false // For self-signed certificates
});

// Database connection is now optional - only for local tracking
const connAttrs = mysql.createConnection({
  host: process.env.DB_HOST,
  port: process.env.DB_PORT,
  user: process.env.DB_USER,
  password: process.env.DB_PASS,
  database: process.env.DB_NAME,
});

// Updated authentication data for older BioStar 2 API format
const authData = {
  User: {
    login_id: process.env.BIO_login_id,
    password: process.env.BIO_password,
  },
};

const logStream = fs.createWriteStream("log.txt", { flags: "a" });

const today = new Date();
const year = today.getFullYear();
const month = (today.getMonth() + 1).toString().padStart(2, "0");
const formattedLogDate = `${year}${month}`;


// Database connection is now optional - only for local tracking
const connectToDB = () => {
  return new Promise((resolve, reject) => {
    // Check if database connection is configured
    if (!process.env.DB_HOST || !process.env.DB_USER) {
      console.log("Database connection not configured - running in API-only mode");
      resolve();
      return;
    }

    connAttrs.connect((err) => {
      if (err) {
        console.log("Database connection failed - continuing in API-only mode");
        console.log("Database error:", err.message);
        resolve(); // Don't reject, just continue without DB
      } else {
        console.log("Connected to local tracking database!");
        resolve();
      }
    });
  });
};

const delay = (ms) => {
  return new Promise((resolve) => setTimeout(resolve, ms));
};

let sessionId;

// Updated authentication for BioStar 2 API (works with your version)
const biostarAuthentication = async () => {
  try {
    console.log("Attempting BioStar 2 authentication...");
    const response = await fetch(`${process.env.BIOSTAR_BASE_URL}/api/login`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify(authData),
      agent: httpsAgent,
    });

    if (!response.ok) {
      throw new Error(`Authentication failed: ${response.status} ${response.statusText}`);
    }

    // Get session ID from response headers
    sessionId = response.headers.get("bs-session-id");
    
    if (!sessionId) {
      throw new Error("No bs-session-id found in response headers");
    }

    console.log(`Authentication successful. Session ID: ${sessionId}`);
    const data = await response.json();
    return sessionId;
  } catch (error) {
    console.error("Authentication error:", error);
    sysLogs(`Authentication failed: ${error.message}`);
    throw error;
  }
};

// Updated user creation for your BioStar 2 version
const biostarCreateUser = async (sessionId, userData) => {
  try {
    // Use only the minimal, correct format as per API docs
    const createUserData = {
      User: {
        user_id: userData.user_id,
        name: userData.name,
        email: userData.email,
        start_datetime: "2001-01-01T00:00:00.00Z",
        expiry_datetime: "2030-12-31T23:59:00.00Z",
        user_group_id: {
          id: "1052" // User Group ID for "Gym Members"
        },
        disabled: userData.disabled === "true",
        access_groups: [
          {
            id: "3" // Access Group ID for "Gym Members"
          }
        ]
      }
    };

    // console.log(`Creating user: ${userData.user_id}`);
    // console.log("User creation payload:", JSON.stringify(createUserData, null, 2));

    const response = await fetch(`${process.env.BIOSTAR_BASE_URL}/api/users`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "bs-session-id": sessionId,
      },
      body: JSON.stringify(createUserData),
      agent: httpsAgent,
    });

    if (!response.ok) {
      const errorText = await response.text();
      // If user already exists, throw a specific error
      if (response.status === 400 && errorText.includes('User who has same id already exists')) {
        throw new Error('User who has same id already exists');
      }
      throw new Error(`User creation failed: ${response.status} ${response.statusText} - ${errorText}`);
    }

    const data = await response.json();
    // console.log(`User ${userData.user_id} created successfully`);
    let msg = `User ${userData.user_id} CREATED SUCCESSFULLY in Gym Members group`;
    sysLogs(msg);
    return data;
  } catch (error) {
    console.error(`Error creating user ${userData.user_id}:`, error);
    let msg = `Failed to create user ${userData.user_id}: ${error.message}`;
    sysLogs(msg);
    await delay(5000);
    throw error;
  }
};

// Updated user update for your BioStar 2 version
const biostarUpdateUser = async (sessionId, userDataUpdate) => {
  try {
    // First, get current user data
    const getUserResponse = await fetch(`${process.env.BIOSTAR_BASE_URL}/api/users/${userDataUpdate.user_id}`, {
      method: "GET",
      headers: {
        "Content-Type": "application/json",
        "bs-session-id": sessionId,
      },
      agent: httpsAgent,
    });

    if (!getUserResponse.ok) {
      throw new Error(`Failed to get user ${userDataUpdate.user_id}: ${getUserResponse.status}`);
    }

    const currentUserData = await getUserResponse.json();
    
    // Handle different response formats and prepare update data
    let updateData;
    
    if (currentUserData.User) {
      // Old format (with User wrapper) - send only updatable fields
      updateData = {
        User: {
          user_id: userDataUpdate.user_id,
          disabled: userDataUpdate.disabled === "true",
          access_groups: [
            {
              id: userDataUpdate.access_group_id || "3"
            }
          ]
        }
      };
    } else {
      // New format or direct format - send only updatable fields
      updateData = {
        user: {
          user_id: userDataUpdate.user_id,
          disabled: userDataUpdate.disabled === "true"
        },
        access_groups: [
          {
            id: userDataUpdate.access_group_id || "3"
          }
        ]
      };
    }

    // console.log("Update payload for user", userDataUpdate.user_id, ":", JSON.stringify(updateData, null, 2));

    // console.log(`Updating user: ${userDataUpdate.user_id} - disabled: ${userDataUpdate.disabled}`);

    const response = await fetch(`${process.env.BIOSTAR_BASE_URL}/api/users/${userDataUpdate.user_id}`, {
      method: "PUT",
      headers: {
        "Content-Type": "application/json",
        "bs-session-id": sessionId,
      },
      body: JSON.stringify(updateData),
      agent: httpsAgent,
    });

    if (!response.ok) {
      const errorText = await response.text();
      throw new Error(`User update failed: ${response.status} ${response.statusText} - ${errorText}`);
    }

    const data = await response.json();
    let msg = `User ${userDataUpdate.user_id} status updated to disabled: ${userDataUpdate.disabled}`;
    sysLogs(msg);
    return data;
  } catch (error) {
    console.error(`Error updating user ${userDataUpdate.user_id}:`, error);
    let msg = `Failed to update user ${userDataUpdate.user_id}: ${error.message}`;
    sysLogs(msg);
    await delay(5000);
    // Note: Removed recursive retry to prevent infinite loops
    throw error;
  }
};

// Check if user exists in BioStar 2
const biostarGetUser = async (sessionId, userId) => {
  try {
    const response = await fetch(`${process.env.BIOSTAR_BASE_URL}/api/users/${userId}`, {
      method: "GET",
      headers: {
        "Content-Type": "application/json",
        "bs-session-id": sessionId,
      },
      agent: httpsAgent,
    });

    if (response.status === 404) {
      return null; // User not found
    }

    if (!response.ok) {
      throw new Error(`Failed to get user: ${response.status} ${response.statusText}`);
    }

    const data = await response.json();
    
    // Debug: Log the response structure for the first user
    if (!biostarGetUser.debugLogged) {
    //   console.log("BioStar user response structure:", JSON.stringify(data, null, 2));
      biostarGetUser.debugLogged = true;
    }

    return data;
  } catch (error) {
    console.error(`Error getting user ${userId}:`, error);
    return null;
  }
};

const performDataOperation = async () => {
  try {
    console.log("Fetching gym master data...");
    const response = await fetch(process.env.GMS_API_URL);
    
    if (!response.ok) {
      throw new Error(`GMS API failed: ${response.status} ${response.statusText}`);
    }

    const data = await response.json();
    if (!data.result || !Array.isArray(data.result)) {
      let msg = `GMS API response missing 'result' array: ${JSON.stringify(data)}`;
      sysLogs(msg);
      console.error("GMS API response:", JSON.stringify(data, null, 2));
      throw new Error(msg);
    }
    console.log(`Found ${data.result.length} users in gym master`);

    for (const user of data.result) {
      if (user.companyid === 3) {
        // console.log(`Processing user: ${user.id}`);
        
        // Check if user exists in BioStar 2
        let existingUser;
        try {
          existingUser = await biostarGetUser(sessionId, user.id);
        } catch (err) {
          console.error(`Error checking existence for user ${user.id}:`, err);
          sysLogs(`Error checking existence for user ${user.id}: ${err.message}`);
          // Skip creation if we can't confirm existence
          continue;
        }

        const owingStatus = parseFloat(user.owing.replace(/[^\d.-]/g, ""));
        const newStatus = owingStatus <= 0.0 && user.status === "Current" ? "false" : "true";
        
        // Everyone stays in the same groups - we only change enabled/disabled status
        // User Group: 1052 (Gym Members) - for organization
        // Access Group: 3 (Gym Members) - for permissions

        if (existingUser) {
          // User exists, check if update is needed
          // Handle different response formats based on your BioStar version
          let currentStatus;
          
          if (existingUser.User) {
            // Old format response (with User wrapper)
            currentStatus = existingUser.User.disabled === "true" || existingUser.User.disabled === true ? "true" : "false";
          } else if (existingUser.user) {
            // New format response (with user property)
            currentStatus = existingUser.user.disabled === "true" || existingUser.user.disabled === true ? "true" : "false";
          } else {
            // Direct format response
            currentStatus = existingUser.disabled === "true" || existingUser.disabled === true ? "true" : "false";
          }

          if (newStatus !== currentStatus) {
            const userDataUpdate = {
              user_id: user.id,
              disabled: newStatus,
              access_group_id: "3" // Keep in Gym Members access group
            };
            await biostarUpdateUser(sessionId, userDataUpdate);
          } else {
            // console.log(`No update needed for user ${user.id}`);
          }
        } else {
          // User not found, create new user
        //   console.log(`User ${user.id} not found, creating new user...`);
          const userData = {
            name: user.firstname && user.surname ? 
                  `${user.firstname} ${user.surname}`.replace(/['`]/g, '') : 'Unknown User',
            email: user.email || `user${user.id}@gym.local`,
            phone: user.phonecell || '',
            user_id: user.id,
            disabled: newStatus,
          };

          if (userData.name !== 'Unknown User' && userData.email) {
            try {
              await biostarCreateUser(sessionId, userData);
            } catch (error) {
              // If error is 'user already exists', do not retry
              if (
                error.message &&
                error.message.includes('User who has same id already exists')
              ) {
                let msg = `User ${userData.user_id} already exists, skipping creation.`;
                sysLogs(msg);
                console.log(msg);
              } else {
                let msg = `Failed to create user ${userData.user_id}: ${error.message}`;
                sysLogs(msg);
                console.error(msg);
              }
            }
          } else {
            let msg = `User ${userData.user_id} cannot be created because Name or Email is invalid`;
            sysLogs(msg);
          }
        }
      }
    }
    console.log("Data operation completed successfully");
  } catch (err) {
    let msg = `Error occurred when fetching data from gymmaster: ${err.message}`;
    sysLogs(msg);
    console.error("Data operation error:", err);
    await delay(5000);
    // Note: Removed recursive retry to prevent infinite loops
    throw err;
  }
};


function sysLogs(msg) {
  const now = new Date().toISOString();
  const logMsg = `${now}: ${msg}\n`;
  logStream.write(logMsg);
  process.stdout.write(logMsg);
}

// Graceful shutdown
process.on('SIGINT', () => {
  console.log('Received SIGINT. Graceful shutdown...');
  if (connAttrs.state !== 'disconnected') {
    connAttrs.end();
  }
  logStream.end();
  process.exit(0);
});

process.on('SIGTERM', () => {
  console.log('Received SIGTERM. Graceful shutdown...');
  if (connAttrs.state !== 'disconnected') {
    connAttrs.end();
  }
  logStream.end();
  process.exit(0);
});

// Main execution
async function main() {
  try {
    console.log("Starting BioStar 2 integration with API-based log retrieval...");
    
    // Connect to database (optional for local tracking)
    // await connectToDB();
    
    // Authenticate with BioStar 2
    await biostarAuthentication();
    
    // Initial data sync
    await performDataOperation();
    
    // Set up periodic sync (every 5 minutes)
    setInterval(async () => {
      try {
        console.log("Running periodic sync...");
        await biostarAuthentication(); // Re-authenticate to refresh session
        await performDataOperation();
      } catch (error) {
        console.error("Periodic sync error:", error);
        sysLogs(`Periodic sync failed: ${error.message}`);
      }
    }, 300000); // 5 minutes
    
    console.log("BioStar 2 integration started successfully. Running periodic sync every 5 minutes...");
    console.log("Using API-based log retrieval (encrypted database compatible)");
    console.log("✅ Active Members (owing ≤ 0 + status 'Current') → enabled in Gym Members group");
    console.log("✅ Debtors (owing > 0 or status ≠ 'Current') → disabled in Gym Members group");
    console.log("📋 User Group: 1052 (Gym Members) | Access Group: 3 (Gym Members)");
    
  } catch (error) {
    console.error("Failed to start application:", error);
    sysLogs(`Application startup failed: ${error.message}`);
    process.exit(1);
  }
}

// Start the application
main();